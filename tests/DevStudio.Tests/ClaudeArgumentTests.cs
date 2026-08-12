using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Mcp;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// Headless, a tool that is not allow-listed is a tool that gets asked about, and a question with
/// nobody to answer it comes back to the model as a cancelled call. These pin what reaches the CLI.
/// </summary>
public class ClaudeArgumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-claudeargs-" + Guid.NewGuid().ToString("n"));
    private readonly RecordingRunner _runner = new();

    public ClaudeArgumentTests() => Directory.CreateDirectory(_root);

    private async Task<List<string>> ArgumentsFor(TurnRequest request, bool alreadySandboxed = true)
    {
        var options = Options.Create(new OrchestratorOptions
        {
            HomePath = _root,
            DataPath = _root,
            ContainerIsTheSandbox = alreadySandboxed,
        });
        var cli = new ClaudeCli(
            _runner,
            new StubProbe(),
            new JsonEntityStore<McpServer>(options, NullLogger<JsonEntityStore<McpServer>>.Instance),
            options,
            NullLogger<ClaudeCli>.Instance);

        await foreach (var _ in cli.RunTurnAsync(request, CancellationToken.None))
        {
        }

        return [.. _runner.LastRequest!.Arguments];
    }

    private TurnRequest Turn(IReadOnlyList<string>? allowed = null, IReadOnlyList<string>? servers = null) => new()
    {
        Prompt = "review the MR",
        WorkingDirectory = _root,
        PermissionMode = PermissionMode.AcceptEdits,
        AllowedTools = allowed ?? [],
        McpServerNames = servers ?? [],
    };

    [Fact]
    public async Task The_local_forge_clis_are_allowed_without_asking()
    {
        var arguments = await ArgumentsFor(Turn(allowed: ["Bash(glab:*)", "Bash(gh:*)", "Bash(git:*)"]));

        var start = arguments.IndexOf("--allowedTools");
        Assert.True(start > 0, "no allow-list was passed");
        Assert.Contains("Bash(glab:*)", arguments);
        Assert.Contains("Bash(gh:*)", arguments);
    }

    [Fact]
    public async Task An_mcp_server_the_app_knows_about_is_allowed_by_name()
    {
        var arguments = await ArgumentsFor(Turn(servers: ["gitlab"]));

        Assert.Contains("mcp__gitlab", arguments);
    }

    [Fact]
    public async Task Nothing_is_allow_listed_when_there_is_nothing_to_allow()
    {
        var arguments = await ArgumentsFor(Turn());

        Assert.DoesNotContain("--allowedTools", arguments);
    }

    [Fact]
    public async Task The_cli_is_told_it_is_already_sandboxed()
    {
        await ArgumentsFor(Turn());

        // Its own sandbox is bubblewrap, which cannot create a namespace where unprivileged user
        // namespaces are disabled - and then every command fails before it starts. IS_SANDBOX is
        // the one the bash sandbox reads; CLAUDE_CODE_SANDBOXED only covers the trust prompt.
        Assert.Equal("1", _runner.LastRequest!.Environment!["IS_SANDBOX"]);
        Assert.Equal("1", _runner.LastRequest.Environment["CLAUDE_CODE_SANDBOXED"]);
    }

    [Fact]
    public async Task Running_outside_a_container_leaves_the_clis_own_sandbox_alone()
    {
        await ArgumentsFor(Turn(), alreadySandboxed: false);

        Assert.DoesNotContain("IS_SANDBOX", _runner.LastRequest!.Environment!.Keys);
        Assert.DoesNotContain("CLAUDE_CODE_SANDBOXED", _runner.LastRequest.Environment.Keys);
    }

    [Fact]
    public async Task The_bash_sandbox_is_turned_off_in_the_accounts_settings()
    {
        await ArgumentsFor(Turn());

        // The env var loses to a settings file that asks for the sandbox, and settings live in the
        // home volume, which outlives a rebuild.
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, ".claude", "settings.json")))!;
        Assert.False(settings["sandbox"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Settling_that_setting_leaves_everything_else_in_the_file_alone()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        var path = Path.Combine(directory.FullName, "settings.json");
        await File.WriteAllTextAsync(path, """
            {"model":"opus","sandbox":{"enabled":true,"excludedCommands":["ls"]}}
            """);

        await ArgumentsFor(Turn());

        var settings = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.False(settings["sandbox"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("opus", settings["model"]!.GetValue<string>());
        Assert.Single(settings["sandbox"]!["excludedCommands"]!.AsArray());
    }

    [Fact]
    public async Task Nothing_is_written_when_the_clis_own_sandbox_is_wanted()
    {
        await ArgumentsFor(Turn(), alreadySandboxed: false);

        Assert.False(File.Exists(Path.Combine(_root, ".claude", "settings.json")));
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

    private sealed class StubProbe : IMcpProbeService
    {
        public Task<McpProbeResult> ListToolsAsync(McpServer server, CancellationToken ct = default) =>
            Task.FromResult(new McpProbeResult(true, "stub", []));

        public Task<McpToolCallResult> CallToolAsync(
            McpServer server,
            string toolName,
            string? argumentsJson,
            CancellationToken ct = default) =>
            Task.FromResult(new McpToolCallResult(true, "stub"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
