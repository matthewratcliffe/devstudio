using System.Text.Json;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;

namespace DevStudio.Tests;

/// <summary>
/// Everything crossing the hub is serialised by System.Text.Json, and a type that quietly fails to
/// round-trip does not show up until two machines are talking to each other — where it looks like a
/// remote turn losing its model, or an agent running with no permissions.
///
/// These are the types that actually go over the wire, checked against the serialiser SignalR uses.
/// </summary>
public sealed class RemoteWireContractTests
{
    /// <summary>SignalR's default JSON protocol options.</summary>
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Wire), Wire)!;

    /// <summary>
    /// The most important one. A turn request that lost its allow-list would run headless with tools
    /// it cannot get permission for; one that lost its home directory would run as the wrong login.
    /// </summary>
    [Fact]
    public void A_turn_request_survives_the_wire()
    {
        var request = new TurnRequest
        {
            Prompt = "do the thing",
            WorkingDirectory = "/remote/work",
            PermissionMode = PermissionMode.AcceptEdits,
            Model = "claude-opus-5",
            Effort = "high",
            McpServerNames = ["orchestrator", "github"],
            AllowedTools = ["Bash(git status)", "Read"],
            SystemPrompt = "be brief",
            ResumeSessionId = "provider-session-1",
            HomeDirectory = "/home/one",
            FallbackHomeDirectory = "/home/two",
            Environment = new Dictionary<string, string> { ["TERM"] = "dumb" },
            ExtraArguments = "--verbose",
        };

        var result = RoundTrip(request);

        Assert.Equal(request.Prompt, result.Prompt);
        Assert.Equal(request.WorkingDirectory, result.WorkingDirectory);
        Assert.Equal(PermissionMode.AcceptEdits, result.PermissionMode);
        Assert.Equal(request.Model, result.Model);
        Assert.Equal(request.Effort, result.Effort);
        Assert.Equal(request.McpServerNames, result.McpServerNames);
        Assert.Equal(request.AllowedTools, result.AllowedTools);
        Assert.Equal(request.SystemPrompt, result.SystemPrompt);
        Assert.Equal(request.ResumeSessionId, result.ResumeSessionId);
        Assert.Equal(request.HomeDirectory, result.HomeDirectory);
        Assert.Equal(request.FallbackHomeDirectory, result.FallbackHomeDirectory);
        Assert.Equal("dumb", result.Environment["TERM"]);
        Assert.Equal(request.ExtraArguments, result.ExtraArguments);
    }

    /// <summary>
    /// Every kind of event a turn streams back. A tool call that lost its id could never be paired
    /// with its completion, and the transcript line would never get its duration.
    /// </summary>
    [Theory]
    [InlineData(AgentEventKind.Text)]
    [InlineData(AgentEventKind.Tool)]
    [InlineData(AgentEventKind.SessionId)]
    [InlineData(AgentEventKind.Result)]
    [InlineData(AgentEventKind.Error)]
    [InlineData(AgentEventKind.Log)]
    [InlineData(AgentEventKind.PermissionDenied)]
    [InlineData(AgentEventKind.ToolCompleted)]
    [InlineData(AgentEventKind.Usage)]
    public void Every_kind_of_agent_event_survives_the_wire(AgentEventKind kind)
    {
        var evt = new AgentEvent(kind, "some text")
        {
            ToolName = "Edit",
            ToolCallId = "call-1",
            Edit = new FileEdit("src/thing.cs", "before", "after", "@@ diff @@"),
            Usage = new TokenUsage(10, 20, 30, 40),
            CostUsd = 0.42m,
            DurationMs = 1234,
        };

        var result = RoundTrip(evt);

        Assert.Equal(kind, result.Kind);
        Assert.Equal("some text", result.Text);
        Assert.Equal("Edit", result.ToolName);
        Assert.Equal("call-1", result.ToolCallId);
        Assert.Equal("src/thing.cs", result.Edit!.Path);
        Assert.Equal("before", result.Edit.Before);
        Assert.Equal("after", result.Edit.After);
        Assert.Equal("@@ diff @@", result.Edit.UnifiedDiff);
        Assert.Equal(100, result.Usage!.Total);
        Assert.Equal(0.42m, result.CostUsd);
        Assert.Equal(1234, result.DurationMs);
    }

    /// <summary>
    /// The plan carries the whole agent plus the project's files. If the agent's collections did not
    /// survive, the workspace built over there would have no skills and no MCP config.
    /// </summary>
    [Fact]
    public void A_workspace_plan_survives_the_wire_with_its_files()
    {
        var plan = new WorkspacePlan
        {
            Agent = new Agent
            {
                Name = "worker",
                Provider = AiProvider.Codex,
                SkillIds = ["skill-1", "skill-2"],
                McpServerIds = ["mcp-1"],
                Environment = new Dictionary<string, string> { ["KEY"] = "value" },
                UseWorktree = true,
            },
            SessionId = "session-1",
            RepositoryId = "repo-1",
            BaseBranch = "develop",
            ProjectId = "project-1",
            ExtraServerIds = ["mcp-2"],
            ProjectFiles = [new SuppliedFile("brief.md", "the brief"u8.ToArray())],
        };

        var result = RoundTrip(plan);

        Assert.Equal("worker", result.Agent.Name);
        Assert.Equal(AiProvider.Codex, result.Agent.Provider);
        Assert.Equal(["skill-1", "skill-2"], result.Agent.SkillIds);
        Assert.Equal(["mcp-1"], result.Agent.McpServerIds);
        Assert.Equal("value", result.Agent.Environment["KEY"]);
        Assert.True(result.Agent.UseWorktree);

        Assert.Equal("repo-1", result.RepositoryId);
        Assert.Equal("develop", result.BaseBranch);
        Assert.Equal("project-1", result.ProjectId);
        Assert.Equal(["mcp-2"], result.ExtraServerIds);

        var file = Assert.Single(result.ProjectFiles);
        Assert.Equal("brief.md", file.FileName);
        Assert.Equal("the brief", System.Text.Encoding.UTF8.GetString(file.Content));
    }

    [Fact]
    public void The_host_config_survives_the_wire()
    {
        var config = new RemoteHostConfig(
            "DESK-01",
            "1.2.3",
            [new RemoteCliDescriptor(AiProvider.Claude, null, "Claude Code", ["opus", "sonnet"], ["high", "low"])],
            [new RemoteNamedItem("mcp-1", "github", true)],
            [new RemoteNamedItem("skill-1", "review")],
            [new RemoteNamedItem("repo-1", "site", false, "main")],
            [new RemoteNamedItem("acct-1", "work", false, "Claude")],
            IsWindows: true);

        var result = RoundTrip(config);

        Assert.Equal("DESK-01", result.HostName);
        Assert.Equal("1.2.3", result.HostVersion);
        Assert.Equal(["opus", "sonnet"], result.Clis[0].Models);
        Assert.Equal(["high", "low"], result.Clis[0].Efforts);
        Assert.Equal("Claude", result.Clis[0].Key);
        Assert.True(result.McpServers[0].IsDefault);
        Assert.Equal("main", result.Repositories[0].Detail);
        Assert.True(result.IsWindows);
    }

    /// <summary>
    /// A user-defined CLI is keyed by its definition id on both sides, so a selection made against a
    /// remote's list means the same thing when it comes back as a turn request.
    /// </summary>
    [Fact]
    public void A_user_defined_cli_keeps_its_key_across_the_wire()
    {
        var descriptor = new RemoteCliDescriptor(AiProvider.Custom, "def-9", "My CLI", [], []);

        Assert.Equal("custom:def-9", RoundTrip(descriptor).Key);
    }

    [Fact]
    public void The_pairing_exchange_survives_the_wire()
    {
        var request = RoundTrip(new RemotePairingRequest("id-1", "laptop", "LAPTOP-01", "1.0.0"));
        Assert.Equal("id-1", request.InstanceId);
        Assert.Equal("LAPTOP-01", request.MachineName);

        var expires = DateTimeOffset.UtcNow.AddDays(1);
        var response = RoundTrip(new RemotePairingResponse("req-1", "123456", "approved", "token", expires, "DESK-01", "1.2.3"));

        Assert.Equal("approved", response.Status);
        Assert.Equal("token", response.Token);
        Assert.Equal("123456", response.VerificationCode);
        Assert.Equal(expires.ToUnixTimeMilliseconds(), response.ExpiresAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void The_terminal_types_survive_the_wire()
    {
        var start = RoundTrip(new RemoteTerminalStart(
            "/bin/sh",
            ["-lc", "echo hi"],
            "/work",
            new Dictionary<string, string> { ["TERM"] = "dumb" },
            true));

        Assert.Equal(["-lc", "echo hi"], start.Arguments);
        Assert.Equal("dumb", start.Environment!["TERM"]);
        Assert.True(start.PreferPseudoTerminal);

        var state = RoundTrip(new RemoteTerminalState("t-1", false, 3, "output", ["https://x"], ["ABCD"]));

        Assert.False(state.IsRunning);
        Assert.Equal(3, state.ExitCode);
        Assert.Equal("output", state.Buffer);
        Assert.Equal(["https://x"], state.DetectedUrls);
        Assert.Equal(["ABCD"], state.DetectedCodes);
    }

    /// <summary>
    /// The shell is chosen from what the far side reported, not from the machine doing the asking —
    /// a Windows desktop driving a Linux container must not send it <c>cmd /c</c>.
    /// </summary>
    [Theory]
    [InlineData(true, "cmd.exe", "/c")]
    [InlineData(false, "/bin/sh", "-lc")]
    public void The_shell_follows_the_far_sides_platform(bool isWindows, string expectedShell, string expectedFlag)
    {
        var config = RemoteHostConfig.Empty("host") with { IsWindows = isWindows };

        var (fileName, arguments) = config.ShellFor("echo hi");

        Assert.Equal(expectedShell, fileName);
        Assert.Equal(expectedFlag, arguments[0]);
        Assert.Equal("echo hi", arguments[1]);
    }
}
