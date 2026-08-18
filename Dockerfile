# syntax=docker/dockerfile:1

# ---- Build ------------------------------------------------------------------
# Pinned to the builder's own architecture: the SDK runs natively and cross-compiles for
# TARGETARCH, which is far quicker than emulating the whole toolchain under QEMU.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# Stamped into the assembly so the UI can show which build this is, and compare it against the
# newest published release. Left at 0.0.0 for a local build, which the update check reads as
# "unversioned" and stays quiet about.
ARG VERSION=0.0.0
WORKDIR /src

COPY DevStudio.slnx ./
COPY src/DevStudio.Domain/DevStudio.Domain.csproj src/DevStudio.Domain/
COPY src/DevStudio.Application/DevStudio.Application.csproj src/DevStudio.Application/
COPY src/DevStudio.Infrastructure/DevStudio.Infrastructure.csproj src/DevStudio.Infrastructure/
COPY src/DevStudio.Ui/DevStudio.Ui.csproj src/DevStudio.Ui/
COPY tests/DevStudio.Tests/DevStudio.Tests.csproj tests/DevStudio.Tests/
RUN ARCH=$(case "$TARGETARCH" in arm64) echo arm64;; *) echo x64;; esac) && dotnet restore src/DevStudio.Ui/DevStudio.Ui.csproj -a "$ARCH"

COPY . .

# Publish restores again on purpose: with --no-restore after a csproj-only restore, the SDK omits the
# framework static web assets and the published app ends up with no _framework/blazor.web.js — which
# leaves every page rendered but completely inert.
RUN ARCH=$(case "$TARGETARCH" in arm64) echo arm64;; *) echo x64;; esac) && dotnet publish src/DevStudio.Ui/DevStudio.Ui.csproj -c Release -a "$ARCH" -p:Version="$VERSION" --no-self-contained -o /app/publish

# Fail the build rather than ship a UI where nothing works.
RUN test -f /app/publish/wwwroot/_framework/blazor.web.js     || (echo 'FATAL: blazor.web.js missing from publish output' && exit 1)

# ---- Runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# The orchestrator shells out to these; the image is useless without them.
#   git + util-linux : worktrees, and `script` for the pty the CLI logins need
#   gh / glab        : agents interact with GitHub and GitLab; private clones use their credential helpers
#   node             : both AI CLIs ship as npm packages
#   ripgrep          : the claude CLI's search tool
ARG GLAB_VERSION=1.113.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg git util-linux less ripgrep unzip jq procps \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli.gpg \
    && chmod go+r /etc/apt/keyrings/githubcli.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends gh nodejs \
    && curl -fsSL "https://gitlab.com/gitlab-org/cli/-/releases/v${GLAB_VERSION}/downloads/glab_${GLAB_VERSION}_linux_$(dpkg --print-architecture).deb" -o /tmp/glab.deb \
    && apt-get install -y --no-install-recommends /tmp/glab.deb \
    && rm -f /tmp/glab.deb \
    && npm install -g @anthropic-ai/claude-code @openai/codex opencode-ai \
    && npm cache clean --force \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# Non-root, with a home directory that is a volume so CLI logins survive a rebuild.
RUN useradd --create-home --home-dir /home/orchestrator --shell /bin/bash orchestrator \
    && mkdir -p /data /home/orchestrator/.claude /home/orchestrator/.codex /home/orchestrator/.config \
    && chown -R orchestrator:orchestrator /data /home/orchestrator

WORKDIR /app
COPY --from=build /app/publish .
RUN chown -R orchestrator:orchestrator /app

USER orchestrator
# The container, the per-session worktree and the permission mode are the isolation here, so the
# CLIs are told not to wrap commands in bubblewrap as well: it needs unprivileged user namespaces,
# which many hosts disable, and without them every command fails before it starts.
ENV HOME=/home/orchestrator \
    IS_SANDBOX=1 \
    CLAUDE_CODE_SANDBOXED=1 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_HTTP_PORTS=7080 \
    ASPNETCORE_ENVIRONMENT=Production \
    GIT_TERMINAL_PROMPT=0

# git refuses to touch worktrees it thinks belong to another user.
RUN git config --global --add safe.directory '*' \
    && git config --global init.defaultBranch main

EXPOSE 7080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:7080/healthz || exit 1

ENTRYPOINT ["dotnet", "DevStudio.Ui.dll"]
