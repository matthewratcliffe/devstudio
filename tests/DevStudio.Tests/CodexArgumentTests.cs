using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// `codex exec` and `codex exec resume` take different flags, and codex exits with a usage error
/// on one it does not recognise rather than ignoring it. Every reply after the first resumes, so
/// getting this wrong breaks a conversation from its second message onwards.
/// </summary>
public class CodexArgumentTests
{
    /// <summary>Flags codex rejects on `exec resume`, checked against codex-cli 0.145.</summary>
    private static readonly string[] RejectedWhenResuming = ["--cd", "-C", "--sandbox", "-s", "--add-dir"];

    private static string Workspace { get; } = Path.Combine(Path.GetTempPath(), "devstudio-codexargs");

    private static async Task<List<string>> ArgumentsFor(TurnRequest request, bool containerIsTheSandbox = true)
    {
        var runner = new RecordingRunner();
        var cli = new CodexCli(
            runner,
            Options.Create(new OrchestratorOptions
            {
                HomePath = "/home/test",
                CodexExecutable = "codex",
                ContainerIsTheSandbox = containerIsTheSandbox,
            }),
            NullLogger<CodexCli>.Instance);

        await foreach (var _ in cli.RunTurnAsync(request, CancellationToken.None))
        {
        }

        return [.. runner.LastRequest!.Arguments];
    }

    private static async Task<string?> StandardInputFor(TurnRequest request, bool containerIsTheSandbox = true)
    {
        var runner = new RecordingRunner();
        var cli = new CodexCli(
            runner,
            Options.Create(new OrchestratorOptions
            {
                HomePath = "/home/test",
                CodexExecutable = "codex",
                ContainerIsTheSandbox = containerIsTheSandbox,
            }),
            NullLogger<CodexCli>.Instance);

        await foreach (var _ in cli.RunTurnAsync(request, CancellationToken.None))
        {
        }

        return runner.LastRequest!.StandardInput;
    }

    private static TurnRequest Turn(string? resumeId = null, PermissionMode mode = PermissionMode.AcceptEdits) => new()
    {
        Prompt = "hello",
        WorkingDirectory = Workspace,
        PermissionMode = mode,
        ResumeSessionId = resumeId,
    };

    [Fact]
    public async Task A_first_turn_sets_the_working_directory_and_sandbox_by_flag()
    {
        var arguments = await ArgumentsFor(Turn(), containerIsTheSandbox: false);

        Assert.Equal("exec", arguments[0]);
        Assert.DoesNotContain("resume", arguments);
        Assert.Contains("--cd", arguments);
        Assert.Equal("workspace-write", arguments[arguments.IndexOf("--sandbox") + 1]);
    }

    [Fact]
    public async Task A_resumed_turn_passes_nothing_codex_would_reject()
    {
        var arguments = await ArgumentsFor(Turn(resumeId: "019ff515-c41c-7863-990c-3fd2a320d974"));

        Assert.Equal(["exec", "resume", "019ff515-c41c-7863-990c-3fd2a320d974"], arguments.Take(3));

        foreach (var flag in RejectedWhenResuming)
            Assert.DoesNotContain(flag, arguments);
    }

    [Fact]
    public async Task A_resumed_turn_still_asks_for_the_same_sandbox()
    {
        var arguments = await ArgumentsFor(Turn(resumeId: "abc", mode: PermissionMode.AcceptEdits), containerIsTheSandbox: false);

        Assert.Contains("sandbox_mode=\"workspace-write\"", arguments);
    }

    [Fact]
    public async Task An_unrestricted_resume_keeps_the_bypass_flag_codex_does_accept()
    {
        var arguments = await ArgumentsFor(Turn(resumeId: "abc", mode: PermissionMode.Unrestricted));

        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", arguments);
        Assert.DoesNotContain("--sandbox", arguments);
    }

    [Fact]
    public async Task The_prompt_is_read_from_stdin_with_a_dash_as_the_last_argument_when_resuming()
    {
        var arguments = await ArgumentsFor(Turn(resumeId: "abc"));

        Assert.Equal("-", arguments[^1]);
    }

    [Fact]
    public async Task The_prompt_travels_on_stdin_rather_than_the_command_line()
    {
        var stdin = await StandardInputFor(Turn(resumeId: "abc"));

        Assert.Equal("hello", stdin);
    }

    [Fact]
    public async Task An_editing_turn_can_reach_the_network()
    {
        var arguments = await ArgumentsFor(Turn(mode: PermissionMode.AcceptEdits), containerIsTheSandbox: false);

        // glab and gh are useless in a sandbox that cannot open a socket.
        Assert.Contains("sandbox_workspace_write.network_access=true", arguments);
    }

    [Fact]
    public async Task An_editing_turn_does_not_ask_codex_to_sandbox_inside_the_container()
    {
        var arguments = await ArgumentsFor(Turn(mode: PermissionMode.AcceptEdits));

        // codex sandboxes through its own bundled bubblewrap, which cannot create a namespace where
        // the host forbids it - bwrap fails and the command never runs at all.
        Assert.Equal("danger-full-access", arguments[arguments.IndexOf("--sandbox") + 1]);
        Assert.DoesNotContain("workspace-write", arguments);
    }

    [Fact]
    public async Task Outside_a_container_codex_keeps_its_own_sandbox()
    {
        var arguments = await ArgumentsFor(Turn(mode: PermissionMode.AcceptEdits), containerIsTheSandbox: false);

        Assert.Equal("workspace-write", arguments[arguments.IndexOf("--sandbox") + 1]);
        Assert.DoesNotContain("danger-full-access", arguments);
    }

    [Fact]
    public async Task A_read_only_turn_stays_off_the_network()
    {
        var arguments = await ArgumentsFor(Turn(mode: PermissionMode.Plan), containerIsTheSandbox: false);

        Assert.DoesNotContain("sandbox_workspace_write.network_access=true", arguments);
    }

    [Fact]
    public async Task A_headless_turn_says_never_to_ask_for_approval()
    {
        var arguments = await ArgumentsFor(Turn());

        // Left unset, codex asks, nothing answers, and the model is told its call was cancelled.
        var index = arguments.IndexOf("approval_policy=\"never\"");
        Assert.True(index > 0, "the approval policy was not set");
        Assert.Equal("-c", arguments[index - 1]);
    }

    [Fact]
    public async Task An_unrestricted_turn_leaves_approvals_to_the_bypass_flag()
    {
        var arguments = await ArgumentsFor(Turn(mode: PermissionMode.Unrestricted));

        Assert.DoesNotContain("approval_policy=\"never\"", arguments);
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", arguments);
    }

    [Fact]
    public async Task A_resumed_turn_still_says_never_to_ask()
    {
        var arguments = await ArgumentsFor(Turn(resumeId: "abc"));

        Assert.Contains("approval_policy=\"never\"", arguments);
    }

    [Fact]
    public async Task Configured_stdio_servers_are_passed_to_codex_as_config()
    {
        using var workspace = new TemporaryWorkspace(new JsonObject
        {
            ["gitlab"] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = "npx",
                ["args"] = new JsonArray("-y", "@zereight/mcp-gitlab"),
                ["env"] = new JsonObject { ["GITLAB_TOKEN"] = "secret" },
            },
        });

        var arguments = await ArgumentsFor(Turn());

        // Codex reads servers from its own config, so each one is handed over as an override.
        Assert.Contains("mcp_servers.gitlab.command=\"npx\"", arguments);
        Assert.Contains("mcp_servers.gitlab.args=[\"-y\", \"@zereight/mcp-gitlab\"]", arguments);
        Assert.Contains("mcp_servers.gitlab.env={GITLAB_TOKEN = \"secret\"}", arguments);
    }

    [Fact]
    public async Task A_server_name_that_is_not_a_bare_key_is_quoted()
    {
        using var workspace = new TemporaryWorkspace(new JsonObject
        {
            ["my.server"] = new JsonObject { ["type"] = "stdio", ["command"] = "run", ["args"] = new JsonArray() },
        });

        var arguments = await ArgumentsFor(Turn());

        // Unquoted, the dot would be read as another level of nesting.
        Assert.Contains("mcp_servers.\"my.server\".command=\"run\"", arguments);
    }

    [Fact]
    public async Task A_remote_server_is_left_to_the_users_own_codex_config()
    {
        using var workspace = new TemporaryWorkspace(new JsonObject
        {
            ["hosted"] = new JsonObject { ["type"] = "http", ["url"] = "https://example.com/mcp" },
        });

        var arguments = await ArgumentsFor(Turn());

        Assert.DoesNotContain(arguments, a => a.Contains("mcp_servers.hosted"));
    }

    /// <summary>Writes the .mcp.json the workspace would have written, and takes it away after.</summary>
    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _path = Path.Combine(Workspace, ".mcp.json");

        public TemporaryWorkspace(JsonObject servers)
        {
            Directory.CreateDirectory(Workspace);
            File.WriteAllText(_path, new JsonObject { ["mcpServers"] = servers }.ToJsonString());
        }

        public void Dispose() => File.Delete(_path);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public ProcessRequest? LastRequest { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false));
        }

        public Task<int> StreamAsync(
            ProcessRequest request,
            Func<string, bool, CancellationToken, Task> onLine,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(0);
        }
    }
}
