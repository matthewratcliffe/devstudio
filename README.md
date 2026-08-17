# DevStudio

[![CI](https://github.com/matthewratcliffe/devstudio/actions/workflows/ci.yml/badge.svg)](https://github.com/matthewratcliffe/devstudio/actions/workflows/ci.yml)
[![ghcr.io](https://img.shields.io/badge/ghcr.io-devstudio-blue?logo=docker&logoColor=white)](https://github.com/matthewratcliffe/devstudio/pkgs/container/devstudio)

A self-hosted console for running **Claude Code**, **OpenAI Codex**, and **OpenCoder** agents side by side. Create
agents, chat with them concurrently, chain them into scheduled workflows, give them repos and
worktrees to work in, and let them talk back to the orchestrator over MCP.

There is **no API integration anywhere**. Every agent drives the real CLI as a child process, so the
only credential involved is the login you complete in the web UI.

## What it does

| Area | What you get |
| --- | --- |
| **Agents** | Any number, each bound to `claude`, `codex`, `opencoder` or a CLI you define yourself, with its own system prompt, model, permission mode, skills and MCP servers. |
| **Bring your own CLI** | Describe any installed, already-signed-in CLI on the **CLI providers** page — executable, argument template, output format, sign-in command — and it becomes an agent provider. No code change, no API key. |
| **Concurrent chats** | Sessions run in parallel up to a configurable cap. Each session queues its own turns, so you can keep typing while an agent is working. |
| **Team settings** | One git repository holding the team's agents, workflows, skills, schedules and standards. Point every install at it and they all get the same definitions, reviewed and versioned like code instead of retyped on each machine. Anything you make in the UI stays local and is never touched by a sync ([how](#team-settings)). |
| **Model handover** | An agent, or one chat, can open on a strong model and hand over to a cheaper one after N turns — `fable` at high effort to work the plan out, `sonnet` at low to carry it out. The agent can also call the handover itself, the moment the thinking is done, by writing `[CHANGE MODEL]` ([how](#asking-for-the-handover)). |
| **Token minimisation** | Eighteen switchable tactics — terse replies, narrow reads, delegated searching, batched tool calls, scoped tests, staying in scope, failing fast and delta-focused context — composed into the prompt as a section telling the agent how to go about the work. Defaults are configurable in Settings, set per agent, overridable by any chat, and switchable mid-conversation ([how](#token-minimisation)). |
| **Standards** | Global instructions and reference files — setup notes, coding standards — that every agent inherits, staged into each workspace as `./global-files`. |
| **Projects** | Instructions plus uploaded files that reach every agent, session, workflow and schedule in the project. Files are staged into `./project-files` in the workspace. Also where you set the account and the summarisation policy. |
| **Guidance** | Steer an agent that is already working, instead of queueing a turn behind it. Optionally interrupt the turn in flight so the steer lands now. |
| **Multiple accounts** | A personal Claude and a work Claude (and the same for Codex) live side by side as separate credential homes. Pick one per project. |
| **Auto-summarisation** | After N turns a project's sessions roll themselves up and — if you want — restart the CLI conversation from the summary, so long chats stay fast and keep the context that matters. |
| **Workflows** | Ordered steps with a shared run context: every finished step publishes its output as `{{steps.step-name}}`, readable by *all* later steps. Steps sharing an order run at the same time. |
| **Scheduler** | Cron expressions, plain timers, or manual-only saved runs — targeting an agent or a workflow, optionally inside a project folder. |
| **Queues** | Backlogs of work, each drained by one agent or workflow — or by nothing yet, in which case the queue just fills up until you choose. A scheduled bot finds the open merge requests and enqueues them; the dispatcher starts an agent per item, as many at a time as you allow. Items are deduplicated on a key, so a poller can re-report the same thing every run without piling up work. |
| **Repositories** | Clone repos from the UI (or pick from `gh repo list`), and cut a fresh git worktree per session so parallel agents never collide. |
| **Your own checkout** | Bind mount a folder from the host and attach it from the UI ([how](#working-on-the-code-your-ide-has-open)); the desktop build browses every drive on the machine instead. Agents work in the same files your IDE has open, in worktrees cut beside the checkout so you can see them. |
| **GitHub and GitLab** | `gh` and `glab` both ship in the image and share the container login, so agents can open pull or merge requests and read issues. A project picks one forge; it can still have several repositories from it. Set `Orchestrator__GitLabHost` (or `GitHubHost`) for a self-managed instance. |
| **New chat** | A conversation with no project and no agent: type and send. The CLI, permission mode, model and MCP servers sit on the right of the chat and stay changeable once it is running — including swapping CLI mid-conversation. Read-only by default, in a scratch directory. |
| **Output files** | Everything an agent writes in its workspace is listed in the chat, with images previewed inline and every file downloadable. |
| **Skills** | Reusable instruction files, written to `.claude/skills/<slug>/SKILL.md` (and mirrored to `AGENTS.orchestrator.md` for Codex) before a session starts. |
| **MCP — both directions** | Register MCP servers and attach them to agents (`.mcp.json` per workspace), **and** the orchestrator exposes its own MCP server at `/mcp` so agents can list sessions, read another agent's transcript, steer a run, leave notes, start sessions and run workflows. HTTP servers can authenticate with an OAuth client-credentials grant, refreshed automatically, or a pasted bearer token. **Test** connects like a CLI would and lists the tools the server actually offers. Servers attach to agents, and a chat — including a quick chat — can carry extra servers of its own, applied from its next turn. The built-in orchestrator entry cannot be deleted and is restored on start if it goes missing. Its own endpoints require a token this app generates and attaches to every session by itself, so nothing else that reaches the port can steer your agents. |
| **PWA** | Installable, with a themed offline page. |
| **Version in the corner** | The build you are running sits under the sidebar, with a line beside it when a newer release exists. |
| **Desktop app** | Installers for Windows, macOS and Linux that run the whole thing natively — no Docker, no volumes, direct access to your files ([how](#as-a-desktop-app-windows-macos-linux)). Updates download in the background and install when you quit. |
| **Persistence** | JSON files on a Docker volume. No database. A volume backup is the whole backup. |

## Project site

A static site lives in [`docs/`](docs/) and is what GitHub Pages serves. To turn it on: **Settings →
Pages → Source: Deploy from a branch**, branch `main`, folder `/docs`. It will appear at
<https://matthewratcliffe.github.io/devstudio/>.

It is three files and the fonts already used by the app — no build step, no generator, nothing to
install. Edit `docs/index.html` and push.

## Running it

### From the published image (recommended)

Every push to the default branch publishes a multi-architecture image (`linux/amd64` and
`linux/arm64`) to the GitHub Container Registry, so there is nothing to build locally:

```bash
docker run -d \
  --name devstudio \
  -p 7080:7080 \
  -p 1455:1455 \
  -v devstudio-data:/data \
  -v devstudio-home:/home/orchestrator \
  --restart unless-stopped \
  ghcr.io/matthewratcliffe/devstudio:latest
```

Or with Compose — drop `build: .` and point `image:` at the registry:

```yaml
services:
  orchestrator:
    image: ghcr.io/matthewratcliffe/devstudio:latest
    # ...the rest of docker-compose.yml unchanged
```

```bash
docker compose pull && docker compose up -d
```

The package is public, so `docker pull` needs no credentials. If you fork this and keep your package
private, authenticate first with a personal access token that has `read:packages`:

```bash
echo "$GHCR_TOKEN" | docker login ghcr.io -u <your-github-username> --password-stdin
```

#### Which tag to use

| Tag | Points at | Use it when |
| --- | --- | --- |
| `latest` | The newest build of the default branch. | You want to track the project. |
| `main` | Same thing, named after the branch. | Explicit branch tracking. |
| `1.4.2` | The exact release tagged `v1.4.2`. | Reproducible deployments. |
| `1.4` / `1` | Newest patch / newest minor within that line. | Automatic patch or minor updates only. |
| `sha-<full-commit>` | One specific commit. | Pinning to a known-good build, or bisecting. |

Every image also carries a signed [build provenance attestation](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations),
so you can verify it came from this repository's workflow rather than someone's laptop:

```bash
gh attestation verify oci://ghcr.io/matthewratcliffe/devstudio:latest --owner matthewratcliffe
```

### As a desktop app (Windows, macOS, Linux)

Docker is the supported way to run this on a server. On your own machine there is a second option:
an installed app with no container, no volumes, and no bind mounts to configure — agents reach your
files because they are already your files.

| Platform | Download | Notes |
| --- | --- | --- |
| **Windows** | `devStudio-win-Setup.exe` | Per-user, no administrator. Installs the WebView2 runtime if Windows does not already have it. |
| **macOS** (Apple silicon) | `devStudio-osx-arm64-Setup.pkg` | |
| **macOS** (Intel) | `devStudio-osx-x64-Setup.pkg` | |
| **Linux** (x64) | `devStudio-linux-x64.AppImage` | `chmod +x` and run. Needs WebKitGTK — `sudo apt install libwebkit2gtk-4.1-0` on Debian and Ubuntu. |

All four are on the [latest release](https://github.com/matthewratcliffe/devstudio/releases/latest).

It is the same application on every platform: a small shell starts `DevStudio.Ui` — the exact binary
the container runs — on `127.0.0.1` by default, and shows it in the system web view. Windows uses WebView2 and
gets a tray icon, so closing the window leaves the agents running and **Quit** from the tray stops
them. macOS and Linux use [Photino](https://www.tryphotino.io/) — WKWebView and WebKitGTK — where
there is no tray, so closing the window stops the server with it.

A few flags, for the platforms without a menu to click:

```bash
devstudio --check-tools           # what is installed, what is missing, and how to install it
devstudio --update                # install a waiting update now instead of on the next quit
devstudio --listen-local-network  # reachable from other machines from the next launch
devstudio --loopback-only         # back to 127.0.0.1 only (the default)
```

#### What changes without the container

| | Container | Desktop |
| --- | --- | --- |
| Reaching your code | A bind mount, attached from the UI | Every drive on the machine is browsable — `C:`, `D:`, network drives — opening on your home directory |
| State | `devstudio-data` volume | `%LOCALAPPDATA%\devStudio-data`, `~/Library/Application Support/devStudio`, `~/.local/share/devStudio` |
| CLI logins | The `devstudio-home` volume, separate from yours | Your real `~/.claude` and `~/.codex` — the logins you already have |
| Sandbox | The container is the boundary, so the CLIs are told not to build their own | No container, so the CLIs keep their own: codex runs `workspace-write`, claude keeps its bash sandbox |
| `git`, `node`, `claude`, `codex`, `gh`, `glab`, `rg` | Baked into the image | Must be on your PATH — **Check tools…** in the tray menu, or `--check-tools`, says what is missing |
| Updates | You pull a new image; the UI tells you when there is one | Downloaded in the background, installed when you quit |

The sandbox row is the one to read twice. In the container, an agent that runs something reckless
wrecks a container. On the desktop it is running as you, with your files and your credentials. The
CLIs' own sandboxes are left on to compensate, but they are not the same boundary, and permission
modes matter more here than they do in Docker.

State lives outside the install directory, because each update installs a new copy of the application
and removes the old one. Point `DEVSTUDIO_DATA` somewhere else if you would rather keep it elsewhere.

The window uses port 7080 when it is free — same as the container, so a bookmark keeps working — and
any free port otherwise.

#### Reaching it from another machine

The desktop build listens on `127.0.0.1` alone, so nothing but this machine can reach it. Turning
that off binds `0.0.0.0` instead, which is how you drive an agent from a phone or a laptop on the
same network:

* **Windows** — tray menu, **Listen on local network**. It asks first, and ticks when it is on.
* **macOS and Linux** — `devstudio --listen-local-network`, and `--loopback-only` to undo it.
* **Either** — `DEVSTUDIO_LISTEN_LOCAL_NETWORK=1` in the environment, which wins over the saved
  setting and is what a scripted launch should use. The shells then show the toggle as fixed.

The setting is applied when the server child starts, so it takes effect on the next launch rather
than mid-session — restarting the server would take every agent mid-turn with it. It is saved in
`network.json` beside the rest of the state.

Read the [sandbox row above](#what-changes-without-the-container) again before turning it on. A
desktop install runs as you, browses every drive on the machine and uses your real CLI logins, and
the seeded account is still `admin`/`admin` until you change it — [change it](#accounts) first.
Windows will also ask whether to allow the connection through its firewall.

#### Updates that do not interrupt anything

The installed app checks GitHub a couple of minutes after start and every six hours after that. When
there is a newer release it **downloads it in the background and then leaves it alone**: applying an
update restarts the app, and a restart takes every agent mid-turn with it.

The download is installed when you quit, so the next launch is already the new version and no session
was lost to it. Windows says so once, with a tray notification and a menu item that offers to restart
now; macOS and Linux put it in the window title. Nothing installs itself while you are working.

`--update` (or the tray item) applies a waiting update immediately, for when nothing is running.

#### Building the installers yourself

Each platform builds on its own runner — the shell is native code either way, and Velopack's
installers are built by the tools of the platform they target.

```bash
# Windows
dotnet publish src/DevStudio.Desktop -c Release -r win-x64 --self-contained -o artifacts/publish
dotnet publish src/DevStudio.Ui      -c Release -r win-x64 --self-contained -o artifacts/publish/server
vpk pack --packId devStudio --packVersion 1.0.0 --packDir artifacts/publish \
         --mainExe DevStudio.Desktop.exe --icon src/DevStudio.Desktop/app.ico \
         --channel win-x64 --framework webview2 --outputDir artifacts/releases

# macOS and Linux — same two publishes, the Photino shell, and the platform's own channel
dotnet publish src/DevStudio.Desktop.Photino -c Release -r osx-arm64 --self-contained -o artifacts/publish
dotnet publish src/DevStudio.Ui              -c Release -r osx-arm64 --self-contained -o artifacts/publish/server
# macOS wants an .icns; Linux takes the png as it is
mkdir icon.iconset && sips -z 512 512 src/DevStudio.Desktop.Photino/app.png --out icon.iconset/icon_512x512.png
iconutil --convert icns icon.iconset --output app.icns
vpk pack --packId devStudio --packVersion 1.0.0 --packDir artifacts/publish \
         --mainExe devstudio --icon app.icns \
         --channel osx-arm64 --bundleId com.matthewratcliffe.devstudio --outputDir artifacts/releases
```

Two self-contained publishes, the server nested in `server/` where the shell looks for it, so the
target machine needs no .NET installed. That is about 220 MB on disk and a ~100 MB installer.

`.github/workflows/ci.yml` runs exactly those steps as a four-way matrix. Every push to
the default branch builds all four installers and publishes them to a GitHub release of their own,
each on its own Velopack channel — which is the feed the installed app reads, so publishing them is
what makes updates work. No tag needed: the release is tagged `v<major>.<minor>.<run number>`, taking
major and minor from the newest `v*.*.*` tag, so every build outranks the one before it. Pushing a
real tag releases that exact version and lifts the base for the builds that follow.

Nothing is signed yet: SmartScreen will warn on Windows and Gatekeeper will refuse the macOS build
until it is opened from the right-click menu once. Add `--signParams` (Windows) or
`--signAppIdentity` / `--notaryProfile` (macOS) to the `vpk pack` step once you have certificates.

For development, run a shell straight from the repository — either one falls back to whatever
`DevStudio.Ui` build output it can find:

```bash
dotnet build
dotnet run --project src/DevStudio.Desktop          # Windows
dotnet run --project src/DevStudio.Desktop.Photino  # macOS and Linux
```

### Building it yourself

```bash
docker compose up -d --build
```

### First run

Open <http://localhost:7080>, go to **Logins**, and complete the sign-in for Claude, Codex and
GitHub in the embedded terminal. It is a real terminal on every platform — `script` in the container,
ConPTY on Windows — which these CLIs check for before they will start a device-code flow at all. Each one prints a link and, for Codex and GitHub, a one-time code —
both are pulled out of the scrollback and shown above the terminal with a copy button. Credentials
land in the `devstudio-home` volume and survive restarts and rebuilds.

Each account picks its own **sign-in method**, because no single flow suits every network:

| Method | What happens | Notes |
| --- | --- | --- |
| **Browser sign-in** | The CLI prints a link and completes the flow through the browser. | Claude asks you to paste a code back. Codex redirects to `http://localhost:1455/auth/callback` — see below, the orchestrator can take that callback for you. |
| **Device code** | A link plus a short code you type into the browser. | Nothing reaches back into the container, so this works from any machine. Available for Codex and GitHub. |
| **Paste a token** | You paste a key or token into a masked field; it goes straight to the CLI's stdin. | Codex (`--with-api-key`) and GitHub (`--with-token`). The CLI stores it in its own credential store — this app never keeps a copy, and the value never appears in the terminal transcript. |
| **Long-lived token** | Claude only: `claude setup-token` mints a durable credential through the browser. | Best for schedules and workflows that run unattended. |
| **Console account** | Claude only: sign in with an Anthropic Console account billed by API usage. | Instead of a Claude subscription. |

### Working on the code your IDE has open

By default every checkout lives on the `devstudio-data` volume, where nothing on the host can see it.
To point agents at a repository you are editing yourself, bind mount it and tell the app that mount
is attachable. (None of this applies to the [desktop app](#as-a-desktop-app-windows-macos-linux), which has no
container to mount anything into — your repositories are already reachable there.)

```yaml
services:
  orchestrator:
    volumes:
      - devstudio-data:/data
      - devstudio-home:/home/orchestrator
      # Forward slashes on Windows. The path on the left is yours; the one on the right is what
      # the container sees.
      - D:/repos:/host/repos
    environment:
      Orchestrator__LocalRepositoryRoots__0: "/host/repos"
```

The same thing with `docker run` — the list is index-numbered, so each root is its own variable:

```bash
docker run -d \
  --name devstudio \
  -p 7080:7080 \
  -v devstudio-data:/data \
  -v devstudio-home:/home/orchestrator \
  -v D:/repos:/host/repos \
  -v C:/work/client:/host/client \
  -e Orchestrator__LocalRepositoryRoots__0=/host/repos \
  -e Orchestrator__LocalRepositoryRoots__1=/host/client \
  ghcr.io/matthewratcliffe/devstudio:latest
```

Mount and root have to agree: the root is the path *inside* the container, the right-hand side of
the `-v`. A root that is not mounted simply never appears in the picker.

Restart after changing either — the roots are read once at start.

Then **Repositories → Attach local folder**: browse the mount, and any folder holding a `.git`
offers an **Attach** button. Nothing is cloned or copied — the app registers the path as it stands,
reads `origin` and the default branch off it, and marks the repo as a local mount.

`LocalRepositoryRoots` is an allow-list, and the only thing standing between a folder picker in a web
UI and the whole container filesystem. Paths outside it — including anything reached with `..` or
through a symlink — are refused by the browser and by the attach itself. List the mounts, nothing
else.

A few things follow from the checkout being shared:

- **Worktrees land on the mount.** An agent with *Use worktree* on gets a branch of its own in
  `../.devstudio-worktrees/<repo>-<branch>`, beside the checkout rather than inside it. You can open
  that folder in the IDE, diff it, and merge it — while your own working copy stays on your branch.
- **Turn worktrees off and you share one working copy.** The agent then edits the same files you
  have open, and `git` in the container and `git` in the IDE fight over `.git/index.lock`. It works,
  but the worktree is the calmer arrangement.
- **Staged files are excluded automatically.** `global-files/`, `project-files/`, `.mcp.json`,
  `GUIDANCE.md` and the rest are added to that clone's `.git/info/exclude`, so they never appear as
  untracked changes in your editor and never reach a commit. It is the private exclude file, so
  nothing is added to the repository.
- **Line endings.** A repository cloned on Windows and used from a Linux container will produce
  whole-file diffs unless `core.autocrlf` agrees with both. Set it before letting an agent commit.
- **Speed.** Bind mounts from a Windows drive are slow for large repositories. If it hurts, keep the
  checkout in the WSL2 filesystem and mount that path instead — the IDE can open it over `\\wsl$\`
  and the container gets native I/O.

**Detach** removes the registration only. The folder on the mount is never touched.

### Local development

```bash
dotnet run --project src/DevStudio.Ui
```

In `Development` the app stores state under `src/DevStudio.Ui/.devstudio/` and uses your real home
directory, so it picks up the CLI logins you already have.

## Architecture

Clean architecture, dependencies pointing inwards:

```
src/
  DevStudio.Domain          entities only — agents, sessions, projects, workflows, schedules, queues, skills, MCP
  DevStudio.Application     abstractions and orchestration — SessionManager, WorkflowEngine, QueueService, cron parser
  DevStudio.Infrastructure  the outside world — CLI adapters, git, gh, JSON stores, pty terminals (ConPTY and script), scheduler, queue dispatcher
  DevStudio.Ui              Blazor Server console, MCP endpoint, PWA
  DevStudio.Desktop.Core    shared desktop plumbing: the server child, paths, tool preflight, updates
  DevStudio.Desktop         Windows tray shell — WebView2, tray icon, balloon notifications
  DevStudio.Desktop.Photino macOS and Linux shell — WKWebView and WebKitGTK through Photino
tests/
  DevStudio.Tests           cron, templating, persistence, workflow engine, queues
```

The key seam is `IProviderCli`. `ClaudeCli` runs `claude -p --output-format stream-json` and
`CodexCli` runs `codex exec --json`; both translate CLI output into a common `AgentEvent` stream.
`CustomCli` is the third implementation and it is data-driven: it builds its command from a stored
`CliProvider` definition, so a new tool is a form to fill in rather than code to write.

### Adding a CLI

On **CLI providers**, start from a preset (Copilot, Gemini, Cursor) or fill in:

| Field | Meaning |
| --- | --- |
| Executable | On `PATH` inside the container, or an absolute path. |
| Prompt arguments | e.g. `-p {{prompt}}`. Tokens are substituted *per argument*, so a long prompt stays one argument. An argument whose token resolves to empty is dropped, which is how optional flags work. |
| Resume arguments | e.g. `--resume {{sessionId}}`. Empty means each turn starts fresh. |
| Output format | Plain text, or JSON lines with the text / session-id / error properties named (dotted paths allowed). |
| Sign-in | Login, logout and status commands, plus the text that means signed out. |

Presets are starting points — CLI flags move around, so everything stays editable, and **Test** checks
the executable is actually installed before you wait on a failing session. Custom providers get their
own accounts and per-project account selection, exactly like the built-in two.

Anything the built-ins do that a definition cannot express — Claude's streaming tool events, say — is
why those two remain hand-written.

Sessions are **turn-based rather than a long-lived interactive process**: each turn is a fresh CLI
invocation resumed from the provider's own session id, which is what makes restarts, concurrency and
scheduling reliable.

## Workflow context

Given steps named *Implement*, *Review* and *Fix*, the third step can read both earlier outputs:

```
Original task: {{task}}

Review feedback:
{{steps.review}}

The implementation was:
{{steps.implement}}
```

Available in every step: workflow inputs by name, `{{previous}}`, and `{{steps.<slugified-name>}}`
for any earlier step regardless of distance.

## Queues

A schedule fires on a clock. A queue drains a backlog. The two meet where a scheduled bot finds work
and the queue turns each piece of it into an agent run.

Each queue names one **agent** or one **workflow** to process its items, and that is the only thing
that touches them. Several queues can run side by side — one for merge requests reviewed by a strict
agent, one for failing builds handled by a repair workflow, one for support tickets triaged by a
cheap model.

The handler is optional. A queue with nothing set still accepts items and simply holds them, so you
can point a poller at a queue today and decide what should drain it later — the backlog that built
up in the meantime is picked up as soon as you choose. The queue reads **no handler** until then.

### Filling one

Three routes in, all equivalent:

- **A bot on a schedule.** A schedule runs an agent every ten minutes whose job is to look, not to
  act: `glab mr list --state opened`, then one `enqueue` call per merge request it found. Give that
  agent the built-in `orchestrator` MCP server and nothing else.
- **Any agent, over MCP.** `enqueue` is a normal tool. An agent that notices work for somebody else
  can hand it over instead of doing it.
- **By hand.** **Add item** on the queue's page.

### Not doing the same work twice

The poller above re-reports every still-open merge request each time it runs. That is not a bug in
the poller — it is the only thing it can do, because it cannot know what has already been queued.
The queue is what makes it safe.

Every item carries a **key** — the merge request URL, the issue number, whatever identifies the work
to whoever found it. Send the same key for the same thing and the queue decides what to do with the
repeat:

| Duplicates | Behaviour |
| --- | --- |
| **Reject while queued or running** | The default. A repeat is refused while the first is outstanding, and accepted once it has finished — right for work that can come round again, like a merge request reopened after changes. |
| **Reject if ever queued** | A key is processed once, ever. |
| **Accept everything** | No deduplication. Right when items are events rather than things. |

A refusal is a normal answer, not an error: `enqueue` replies that the item is already queued, and
the bot carries on.

### Draining it

The dispatcher wakes every `QueueTickSeconds`, and for each enabled queue **that has a handler**
starts as many items as it has free slots — **items at once** is the cap. Items go out highest
priority first, oldest first within a priority.

An agent queue renders its prompt from the item:

```
Review the merge request at {{item.key}}.

{{item.body}}

The author is {{payload.author}} and it targets {{payload.branch}}.
```

`{{item.title}}`, `{{item.body}}`, `{{item.key}}`, `{{item.priority}}` and every payload entry —
bare or as `{{payload.<name>}}` — are available. Leave the template empty and the agent is handed the
item as it stands, which is enough when the agent's system prompt already describes the job.

A workflow queue passes the same values as run inputs, with the queue's own inputs underneath as
constants the item can override.

The session an item starts belongs to the item. When the agent finishes, the session is marked
**finished** and turns read only: the transcript, its files and whatever the agent said last stay on
the chat page, but the composer is replaced by a note and nothing — you, another agent over MCP, or a
retry — can send it a further turn. The item's recorded outcome is the last word on it, and a turn
sent afterwards would quietly make that outcome wrong. Start a new session when there is more to do.

### When it goes wrong

A failed item is retried up to **tries per item**, waiting **retry delay** between attempts, and then
settles as failed with the error on the row. Failed items stay on the queue so somebody can look at
them; **Retry** puts one back with its history cleared.

Each row links to the session or run that processed it, so the transcript is one click from the
failure. If the orchestrator restarts mid-item, the item is released back to pending on start — the
interrupted attempt still counts, so an item that kills the process cannot loop forever.

Pausing a queue stops dispatch but not arrivals: items keep landing and wait for you to resume.

**Clear finished** removes what has already settled and leaves the outstanding work alone.
**Empty queue** removes everything, cancelling whatever is running. Both keep the queue itself, so a
poller refills it on its next pass — deleting the queue is what stops that.

Queues are local to an install; unlike agents, workflows and schedules they are not part of
[team settings](#team-settings).

## The orchestrator's MCP server

There are two built-in servers. `images` is attached everywhere by default and exposes only
`generate_image` (see [Images](#images)). `orchestrator` is opt-in, because its tools can reach other
sessions.

Attach the built-in `orchestrator` server to an agent and it gains these tools:

`list_agents` · `list_sessions` · `get_session` · `send_message` · `start_session` ·
`send_guidance` · `check_guidance` · `get_notes` · `add_note` · `list_projects` ·
`list_workflows` · `run_workflow` · `list_queues` · `enqueue` · `list_queue_items`

That is how a manager agent supervises worker agents — it can read what they produced, steer them
mid-run, and respond.

Both endpoints are closed to anything that cannot prove it is this app. Requests must carry a bearer
token the app generates itself and keeps on the data volume; anything else gets a 401. Nobody types
it in — it is written into each session's `.mcp.json` when the workspace is prepared, and read fresh
every time, so rotating it from the MCP servers page takes effect without touching any server
record. A browser already signed in to the console is let through as well, which is what keeps the
**Test** button working.

Rotating does not interrupt work. `.mcp.json` is rewritten before every turn, so the only thing left
holding the old token is a turn already in flight — and it read that value at process start, where
nothing can reach in and correct it. So the replaced token stays accepted for slightly longer than a
turn is allowed to run (`TurnTimeoutMinutes` + 5), which covers every turn in flight and nothing
else; the window is written to disk beside the token, so a redeploy mid-turn does not undo it.
**Rotate and cut off now** skips the window for when the token itself has leaked, and any turn
mid-flight loses its MCP tools with a 401.

## Guidance

A turn is one CLI invocation and its prompt cannot be rewritten once the process has it, so a steer
reaches the agent by three routes at once:

1. **On disk** — written to `GUIDANCE.md` in the workspace. Every agent is told to re-read it during
   long tasks.
2. **Over MCP** — a running agent calls `check_guidance` with its own session id and gets whatever is
   waiting. This is the only route that lands *inside* a turn already in progress.
3. **Next turn** — outstanding guidance is folded into the top of the next prompt regardless, so it
   can never be missed.

Ticking **interrupt** stops the turn in flight (just that turn — the session and its queue survive)
and immediately starts a new one carrying the steer.

## One provider per project

A project pins **one AI provider** and **one forge**. Every session inside it runs on that provider
whatever the individual agent is configured with, so a project can never end up half on Claude and
half on Codex; the stored agent is left untouched, the override lives only for the session. The
repository picker narrows to the project's forge, and a project can have as many repositories from it
as you like.

Leave the AI provider unset and each agent keeps its own choice.

## Files an agent produces

The chat side panel lists the session's working directory, newest first, skipping noise like `.git`
and `node_modules`. Images are previewed inline; every file has a download link. The list refreshes
itself when a turn finishes.

Downloads go through `/workspace/{sessionId}/{path}`, which resolves the path and refuses anything
that lands outside that session's own directory — the session id and path both arrive from the
browser, so neither is trusted.

### MCP tools and permission modes

Two things bite when attaching an MCP server to an agent, and the app now handles both:

- **Tools must be allow-listed.** Running headless there is nobody to answer a permission prompt, so
  every attached server's tools are passed as `--allowedTools mcp__<server>`. Without this the CLI
  reports *"Claude requested permissions… but you haven't granted it yet"* and the model concludes the
  service simply is not available.
- **Plan mode refuses MCP tool calls outright**, whatever the tool does. An agent set to Plan sees the
  tools but cannot call them. The agent editor and the chat panel both warn when that combination is
  set, and a quick chat with servers attached automatically runs in Accept edits instead.

Connection status is surfaced too: the transcript shows `mcp:<server> connected`, or an error naming
the server if it failed to start, rather than the tools silently going missing.

### Images

Neither `claude` nor `codex` generates images, so the orchestrator does it for them. The **Images**
page generates from a prompt and keeps a gallery; agents reach the same service through a
`generate_image` tool and the result appears inline in the transcript. Both write to `<DataPath>/images`
and are served from `/images/<file>`.

Three backends sit behind one interface, so running out of free quota means changing which one is
selected rather than changing anything else:

| Backend | Setup | Free allowance |
| --- | --- | --- |
| **Pollinations** | None — this is the default, and it works on a fresh install | Anonymous is watermarked and limited to one image every 15s; a free token from `auth.pollinations.ai` removes both |
| **Cloudflare Workers AI** | Account id and an API token with the Workers AI permission | 10,000 neurons a day, resetting 00:00 UTC — roughly 500 images at 1024×1024 on FLUX.1 Schnell, with no per-request throttle |
| **Gemini** | A Google AI Studio key | Varies by key and region; AI Studio shows yours. The only backend here that *edits* an existing image, and the pro image model needs billing enabled |

Because the anonymous Pollinations tier rejects rather than queues, that backend spaces its own
requests out — a second image in the same turn waits rather than failing.

Keys are set on the **Logins** page, not in configuration, and are stored on the volume with the CLI
accounts. They are re-read before every generation, so a key added mid-session applies to the next
image rather than after a restart.

An agent reaches image generation two ways: the built-in `generate_image` tool on OpenAI-compatible
providers, and a second built-in MCP server called `images`, which is how `claude` and `codex` sessions
get at it. That server is **attached to every session by default** and carries nothing but
`generate_image` — being able to draw a picture should not also mean being able to start sessions and
steer other agents, which is why it is separate from the `orchestrator` server rather than a flag on it.
Note that Plan mode refuses all MCP tool calls, so a read-only chat still cannot generate.

Where the permission mode allows writing, a copy is also dropped in the workspace under
`generated-images/` so the agent can go on to use the file.

The transcript renders the picture inline, with a download link beneath it. It does this for
`![alt](/images/…)` markdown *and* for a bare `/images/…` path written in prose, because a CLI provider
generates through MCP and then describes the result in its own words. Only paths this app serves are
ever turned into an `<img>` — agent output cannot produce a tag pointing at another host. Downloads
arrive named after the prompt (`a-fluffy-ginger-cat.jpg`) rather than the id.

Agents can still produce SVG and mermaid diagrams directly as text, and those preview inline as before.

## Picking rather than typing

Model, thinking level and base branch are all dropdowns rather than free text:

- **Model** is offered per provider from `Orchestrator__ClaudeModels` / `CodexModels` /
  `OpencoderModels`, or the model
  list on a custom CLI definition. Names move faster than this app, so the list is configuration and
  there is always a *Something else…* option that reveals a text box.
- **Thinking level** maps to what each CLI actually accepts — `--effort` for Claude
  (low → max), `model_reasoning_effort` for Codex (minimal → high), and whatever a custom CLI
  declares.
- **Base branch** is read live from the selected repository with `git for-each-ref`, and falls back
  to a text box if the repository cannot be listed.

## Team settings

A team's agents, workflows, skills, schedules and standards belong in review and in version control
like the rest of the work. Put them in a repository, point every install at it on the **Team settings**
page, and each one imports the same definitions.

Clone or attach the repository on the **Repositories** page first, then pick it, name the folder inside
it (`devstudio` by default, blank for the root), and press **Write starter files** to get a working
example of each format:

```
devstudio/
  standards.md                      standing instructions for every session
  agents/builder.json               an agent; skills are named, not referenced by id
  skills/conventional-commits.md    frontmatter name/description/tags, markdown body
  workflows/build-then-review.json  steps naming the agent that runs them
  schedules/weekday-triage.json     cron, naming the agent or workflow it fires
```

Nothing in a file refers to an id, because a GUID this install happened to assign means nothing on
anyone else's machine. Files name things — an agent by name, a skill by slug — and the import resolves
them, reporting in the sync log anything it could not find.

What a sync does, and deliberately does not do:

- **Yours stay yours.** A definition made in the UI is local to that install and is never rewritten,
  reordered or removed. The two sets sit side by side, and team ones carry a `team` pill.
- **A second sync updates.** Every imported record remembers the file it came from, so re-importing
  edits it in place instead of leaving a second copy.
- **Deleting a file removes what it defined**, everywhere — that is the point of one repository owning
  it. Local definitions are untouched by that pass.
- **A schedule arrives paused** unless its file says `"enabled": true`, and turning one off here
  survives every later sync. A repository should not be able to start work overnight on a machine
  whose owner stopped it.
- **A broken file loses only itself.** Malformed JSON is reported in the log and the rest of the commit
  still imports.
- **A schedule with no target here is refused** rather than imported looking healthy and firing at
  nothing.

**Pull before reading** fast-forwards the checkout first; if that fails — no network, local commits —
the checkout is imported as it stands and the log says so. **Sync on start** runs the import in the
background when the app boots, so a machine that has been switched off catches up by itself.

## Instruction layering

Four layers reach an agent's system prompt, least specific first:

    Team standards  →  Standards (global)  →  Project instructions  →  Agent instructions

**Team standards** are the `standards.md` of the [team settings repository](#team-settings), shared by
everyone pointed at it and rewritten by every sync — which is why they sit apart from the ones you
edit here rather than being merged into them.

**Standards** is the page for house rules that never change per project: how code should be written,
how to commit, what to avoid. Files uploaded there are staged into `./global-files` in every
workspace and listed in the prompt so the agent knows to read them; project files land in
`./project-files` the same way. A project that plays by different rules can untick **apply the global
standards and files here** and skip the layer entirely.

### The Codex callback

Codex hard-codes `http://localhost:1455/auth/callback` into its authorise request, which only
resolves on the machine running the container. Two ways round it, both built in:

- **Change the port.** When the browser lands on the dead `localhost:1455` URL, edit the port in the
  address bar to `7080`. This app serves the same `/auth/callback` path and hands it to the CLI.
- **Paste the URL.** The sign-in terminal shows a field for the failed callback URL — paste the whole
  thing and it is delivered for you.

Only the path and query of what you paste are used; the request always goes to loopback on the
configured port, so it cannot be pointed anywhere else. Set `Orchestrator__CliCallbackPort` if you
ever need a different port.

Device code sign-in avoids all of this, which is why it is the default for Codex.

## Token minimisation

The [four instruction layers](#instruction-layering) settle *what* the work is. Token minimisation
settles *how* it is carried out, in a section of its own appended after them and before any guidance:

    Team standards  →  Standards  →  Project instructions  →  Agent instructions  →  Token minimisation  →  Guidance

Eighteen tactics, each switched on or off on its own:

| Tactic | What the agent is told |
| --- | --- |
| **Terse replies** | Answer and stop — no preamble, no restating, no recap of what the reader has just seen. |
| **Read narrowly** | Search first, read the lines that matter with an offset and a limit, never re-read what is already in the conversation. |
| **Delegate searching** | Broad exploration goes to a subagent; only its conclusion comes back into the conversation. |
| **Batch tool calls** | Independent calls go out together, because every round trip re-sends the whole conversation. |
| **Trust tool results** | No reading a file back to confirm an edit that reported success. |
| **Quiet command output** | Filter at the source — `head`, `grep`, a quiet flag — instead of reading the whole log. |
| **Narrow tests** | Run the nearest test that proves the change; the full suite waits for CI. |
| **Plan before editing** | Settle the approach first, because rework costs several times what thinking does. |
| **Stay in scope** | What was asked and nothing beside it; mention the rest instead of building it. |
| **Summarise early** | Reduce long output to its finding and carry that forward, not the raw text. |
| **Edit, don't rewrite** | Change files in place; rewriting one costs its whole length for the sake of a few lines. |
| **Recommend, don't survey** | Give the recommendation and the reason for it, not every option you discarded. |
| **Fail fast** | Two failed goes at the same problem, then report — a retry loop is the most expensive thing a conversation can do. |
| **Hand over when mechanical** | Ask for the cheaper model with `[CHANGE MODEL]` once the decisions are made. |
| **Reuse established context** | Carry forward facts already established instead of asking or reading for them again. |
| **Avoid speculative work** | Follow evidence and the request, not hypothetical branches or improvements. |
| **Minimise tool inputs** | Send only the paths, fields and arguments the call needs. |
| **Prefer deltas** | Ask for diffs, status and summaries instead of unchanged full content. |

They are instructions, not enforcement — nothing here truncates a prompt or blocks a tool call. The
block that carries them says so, and says what they may not buy: the saving comes out of narration,
re-reading and rework, never out of skipping a step, dropping a requirement or guessing at something
the agent could have read.

An agent carries a default selection, set in its editor. Application settings select the defaults for
new agents and quick chats; existing agents keep their own selection. A chat starts out following it and can take
the selection over for itself from the panel on the right, at any point while it runs — the prompt is
composed fresh for every turn, so a tactic switched on or off applies from your next message and
leaves a turn already running under the rules it started with. Ticking **follow the agent** again
hands the choice back.

### Asking for the handover

A [model handover](#what-it-does) is normally a distance in turns: the opening model covers the first
N, the cheaper one covers the rest. The agent can also call it. When a conversation has somewhere
cheaper to go, the prompt tells it so and names the model; writing `[CHANGE MODEL]` anywhere in an
answer moves the conversation over from the next turn.

The turn that asks was written by the model that was already running and cannot be switched under
itself, so the change lands on the turn after it — the transcript says both, rather than leaving you
to assume the answer you are reading came from the cheaper one. It is one-way: nothing the agent says
afterwards moves it back, and only changing the model yourself in the panel does.

Where it goes:

- **The handover's own target**, when one is configured on the agent or the chat. That pair is a
  stated intent about this agent, and the marker only brings it forward.
- **The next model down** the list configured for that CLI otherwise — the lists are written
  strongest first. A conversation running on the CLI's own default has neither, so the marker is
  never offered and nothing happens.

**Hand over when mechanical** is the token-minimisation tactic that asks the agent to use it.

In a [team settings repository](#team-settings) an agent file names its tactics:

```json
"tokenMinimisation": ["PlanFirst", "NarrowReads", "StayInScope"]
```

## Accounts

Each account is a separate home directory holding its own `.claude` / `.codex` credentials, and the
CLI process runs with `HOME` pointed at it. Add accounts on the **Logins** page, log into each one,
then choose per project which Claude and which Codex account its work runs under. Resolution order:

    project's choice for that provider → agent's pinned account → provider default → container home

The seeded `Default` account for each provider points at the container home, so an existing login
keeps working untouched.

## Summarisation

Set **summarise after N turns** on a project. When a session hits the threshold the same agent is
asked to summarise the work, the summary is stored on the session and shown in the transcript, and —
with **restart the conversation from the summary** on — the provider's conversation id is dropped so
the next turn opens a fresh CLI conversation with the summary as its context. Leave it off to keep
the full history and just record the summary.

## Configuration

Everything is under the `Orchestrator` configuration section, overridable with
`Orchestrator__<Key>` environment variables. The defaults below are the container's; the
[desktop app](#as-a-desktop-app-windows-macos-linux) sets its own for paths, the home directory and the sandbox:

| Key | Default | Purpose |
| --- | --- | --- |
| `DataPath` | `/data` | Root of the state volume. |
| `RepositoriesPath` | `/data/repos` | Clones. |
| `WorktreesPath` | `/data/worktrees` | Per-session worktrees. |
| `ScratchPath` | `/data/scratch` | Workspace for agents with no repo or project. |
| `LocalRepositoryRoots` | *(empty)* | Host directories, bind mounted into the container, that may be browsed and attached as repositories. Empty turns the feature off. One indexed env var per root: `Orchestrator__LocalRepositoryRoots__0`, `__1`, … See [Working on the code your IDE has open](#working-on-the-code-your-ide-has-open). |
| `AllowAllLocalDrives` | `false` | Offer every drive on the machine in the folder picker, on top of `LocalRepositoryRoots`. The desktop build sets this true: it runs as you and has no container boundary to protect. Left false in the container, where the only reachable paths should be the mounts somebody declared. |
| `LocalWorktreesFolderName` | `.devstudio-worktrees` | Folder created beside an attached repo that its worktrees are cut into. |
| `HomePath` | `/home/orchestrator` | The default account's home: `~/.claude`, `~/.codex` and the `gh` login. Extra accounts live under `<DataPath>/accounts/`. Empty means the real user home. |
| `MaxConcurrentSessions` | `6` | Cap on agent turns running at once. |
| `TurnTimeoutMinutes` | `60` | A turn is abandoned after this long. |
| `SchedulerTickSeconds` | `20` | How often due schedules are checked. |
| `QueueTickSeconds` | `10` | How often queues are checked for items to start. This is the delay between an item arriving and work beginning on it. |
| `SessionAutoArchiveHours` | `24` | How long a finished session stays in the sessions list before it is archived on its own. Running sessions are never taken, however old, and one you restore from the archive by hand is never taken again. `0` switches the sweep off. |
| `SessionArchiveTickMinutes` | `15` | How often the auto-archive sweep runs. |
| `UpdateCheckEnabled` | `true` | Ask GitHub every six hours whether a newer release exists, and say so under the sidebar. Nothing is downloaded or installed — a container cannot replace itself. The desktop builds set this false, because they update themselves. |
| `UpdateRepository` | `matthewratcliffe/devstudio` | Repository the check reads releases from, as `owner/name`. |
| `PruneEphemeralWorktrees` | `false` | Delete a session's worktree when it finishes. Off, because uncommitted work would go with it. |

## Permission modes

| Mode | Claude | Codex |
| --- | --- | --- |
| Default | `--permission-mode default` | `--sandbox read-only` |
| Plan | `--permission-mode plan` | `--sandbox read-only` |
| Accept edits | `--permission-mode acceptEdits` | `--sandbox workspace-write` |
| No restrictions | `--dangerously-skip-permissions` | `--dangerously-bypass-approvals-and-sandbox` |

Unattended work (schedules, workflows) needs **Accept edits** or higher — in Default mode the CLI
blocks waiting for an approval nobody is there to give. Use it with a worktree.

## Tests

```bash
dotnet test
```

Coverage collection is wired up through `coverlet.collector`, so `dotnet test --collect:"XPlat Code Coverage"`
works without extra packages.

## Continuous integration and releases

Everything is built by one workflow, `.github/workflows/ci.yml`, so a commit that ships is a commit
where the tests, the image and all four installers built — off one version, in one run.

| Job | Runs on | What it does |
| --- | --- | --- |
| **Unit tests** | Every push, every pull request, manual. | Restores, builds and tests the solution on .NET 10, caching NuGet between runs and uploading the `.trx` results as an artifact. |
| **Version** | Default branch, `v*.*.*` tags, manual. | Derives the one version the rest of the run stamps into the image, the installers and the release. |
| **Container image** | Default branch, `v*.*.*` tags, manual. | Builds for `linux/amd64` and `linux/arm64` and pushes to `ghcr.io/<owner>/<repo>`. |
| **Installers** | Default branch, `v*.*.*` tags, manual. | Four runners in parallel: Windows, macOS on Intel and Apple silicon, and Linux. |
| **Publish the release** | Default branch and `v*.*.*` tags. | Attaches all four channels to a GitHub release, one channel at a time. |

Things worth knowing:

- **A pull request runs the tests and nothing else.** Packaging is slow, and four platforms plus a
  two-architecture image is a long wait for a review. The cost is that a break in the `Dockerfile`
  or in packaging surfaces on merge rather than on the PR — `workflow_dispatch` on the branch is
  the way to check before merging when you have touched either.
- **Tests gate everything.** Every other job `needs: test`, so a red suite publishes nothing.
- **Nothing to configure.** It authenticates with the built-in `GITHUB_TOKEN` — no secret to create
  — and derives the image name from `${{ github.repository }}`, lowercased because ghcr.io rejects
  uppercase paths. Fork it and it publishes to your namespace with no edits.
- **The layer cache lives in GitHub Actions cache** (`type=gha`), so the expensive `apt` and `npm`
  layers are reused between runs.
- **Provenance and an SBOM** are attached to every push, plus a separate signed attestation you can
  check with `gh attestation verify`.
- **`workflow_dispatch` takes a `platforms` input** if you want a quick amd64-only build. A dispatch
  off the default branch builds and packages everything, and pushes the image under the branch's
  own tag, but never publishes a release — that would hand it to every installed app.

### Cutting a release

Every push to the default branch is already a release, tagged `v<major>.<minor>.<run number>`. Tag
one yourself only to set a version deliberately:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

That releases `1.0.0` exactly — image tags `1.0.0`, `1.0`, `1` and `sha-<commit>`, installers on
every channel — and lifts the base, so the builds after it are `1.0.<run number>` rather than
dropping back underneath it. `latest` continues to follow the default branch.

### First publish: making the package public

A new GHCR package inherits the repository's visibility, and a package published from a private repo
stays private even if the repo is later made public. To change it: **repository → Packages → the
package → Package settings → Change visibility**. While it is private, anyone pulling needs a token
with `read:packages`.

If the first run fails with `denied: installation not allowed to Create organization package`, the
repository's **Settings → Actions → General → Workflow permissions** is set to read-only; switch it
to read and write, or grant packages write there.

### Why the Dockerfile cross-compiles

The build stage is pinned with `FROM --platform=$BUILDPLATFORM` and publishes with `-a $TARGETARCH`.
The .NET SDK therefore always runs natively on the amd64 runner and merely emits arm64 output,
instead of the whole toolchain being emulated through QEMU — which turns an arm64 build from tens of
minutes into roughly the cost of a second publish. Only the final runtime layer is genuinely arm64.

## Operating it

### Upgrading

```bash
docker compose pull && docker compose up -d
```

State lives in volumes, not in the image, so an upgrade keeps every agent, project, session and
login. Roll back by pinning the previous tag or `sha-<commit>` and running the same two commands.

You do not have to watch for releases: the running build shows its version under the sidebar, and
when a newer one is published a quiet line appears beside it linking to the release. That is all it
does — a container cannot replace itself, so the two commands above stay yours to run. The check
reads one GitHub URL every six hours and is turned off with
`Orchestrator__UpdateCheckEnabled=false`. The [desktop builds](#as-a-desktop-app-windows-macos-linux)
do not use it; they update themselves.

### Backing up

Both volumes matter — `devstudio-data` is the state, `devstudio-home` is the CLI credentials:

```bash
docker run --rm -v devstudio-data:/data -v "$PWD:/backup" alpine \
  tar czf /backup/devstudio-data.tgz -C /data .
```

Restore by extracting the same tarball back into a fresh volume. There is no database, so a file
copy is a complete and consistent backup once the container is stopped.

### Logs and health

```bash
docker compose logs -f orchestrator
curl -fsS http://localhost:7080/healthz
```

The image declares a `HEALTHCHECK` against `/healthz`, so `docker ps` reports the app's own view of
itself rather than merely that the process exists.

### Exposing it beyond localhost

The app has **no authentication of its own** and its agents can run commands and reach your git
forge with your credentials. Treat the port as privileged: keep it on a trusted network, or put it
behind a reverse proxy that terminates TLS and authenticates users. Blazor Server needs WebSockets
proxied — with nginx that means `proxy_set_header Upgrade $http_upgrade;` and
`proxy_set_header Connection "upgrade";` on the location block.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| Pages render but nothing is interactive | `blazor.web.js` missing from the publish output. The Dockerfile asserts it exists and fails the build, so this means you are running a hand-built publish that used `--no-restore`. |
| *"Claude requested permissions… but you haven't granted it yet"* | An MCP server's tools were not allow-listed. Attaching the server through the UI passes `--allowedTools mcp__<server>` for you; a hand-rolled config has to do the same. |
| An agent can see MCP tools but never calls them | The agent is in **Plan** mode, which refuses MCP tool calls outright. Use Accept edits. |
| A scheduled run stalls forever | Default permission mode blocks on an approval prompt nobody can answer. Unattended work needs **Accept edits** or higher, ideally in a worktree. |
| Codex sign-in lands on a dead `localhost:1455` page | Either edit the port in the address bar to `7080`, or paste the whole failed URL into the field on the sign-in terminal. Device code sign-in avoids the problem entirely. |
| `git` refuses to use a worktree inside the container | Only relevant outside this image — the image already sets `safe.directory '*'` for the `orchestrator` user. |
| Logins disappear after a rebuild | The `devstudio-home` volume was not mounted. Credentials live in `/home/orchestrator`. |

## Repository layout

```
.github/workflows/   CI, container publishing and the desktop installer
src/                 seven projects, dependencies pointing inwards
tests/               xUnit suite
Dockerfile           multi-stage, cross-compiling, non-root runtime
docker-compose.yml   the supported way to run it
```

## Licence

[MIT](LICENSE). Use it, fork it, ship it — the only condition is that the copyright
notice travels with it.
