using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Common;
using DevStudio.Domain.Mcp;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.Persistence;
using DevStudio.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// Both CLIs report an answer twice — once as it is written, once in full when it is done — so
/// these check that the transcript shows it once, in pieces, as it arrives.
/// </summary>
public class StreamingTranscriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-stream-" + Guid.NewGuid().ToString("n"));

    private TurnRequest Turn() => new() { Prompt = "hello", WorkingDirectory = _root };

    private ClaudeCli Claude(params string[] lines) =>
        new(new ScriptedRunner(lines),
            new StubProbe(),
            new JsonEntityStore<McpServer>(OptionsFor(), NullLogger<JsonEntityStore<McpServer>>.Instance),
            OptionsFor(),
            NullLogger<ClaudeCli>.Instance);

    private CodexCli Codex(params string[] lines) =>
        new(new ScriptedRunner(lines), OptionsFor(), NullLogger<CodexCli>.Instance);

    private IOptions<OrchestratorOptions> OptionsFor() =>
        Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root });

    private static async Task<string> TextOf(IProviderCli cli, TurnRequest request)
    {
        var text = string.Empty;
        await foreach (var evt in cli.RunTurnAsync(request, CancellationToken.None))
        {
            if (evt.Kind == AgentEventKind.Text)
                text += evt.Text;
        }

        return text;
    }

    private static async Task<List<AgentEvent>> EventsOf(IProviderCli cli, TurnRequest request)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in cli.RunTurnAsync(request, CancellationToken.None))
            events.Add(evt);

        return events;
    }

    [Fact]
    public async Task Claude_streams_text_a_delta_at_a_time()
    {
        var cli = Claude(
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello "}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"world"}}}""");

        var events = await EventsOf(cli, Turn());

        Assert.Equal(["Hello ", "world"], events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text));
    }

    [Fact]
    public async Task Claude_does_not_repeat_the_finished_block_that_follows_its_deltas()
    {
        var cli = Claude(
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello "}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"world"}}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello world"}]}}""");

        Assert.Equal("Hello world", await TextOf(cli, Turn()));
    }

    [Fact]
    public async Task Claude_still_reports_tool_calls_from_the_finished_message()
    {
        var cli = Claude(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"ls"}}]}}""");

        var events = await EventsOf(cli, Turn());

        Assert.Contains(events, e => e.Kind == AgentEventKind.Tool && e.ToolName == "Bash");
    }

    [Fact]
    public async Task Claude_ignores_deltas_that_are_not_text()
    {
        var cli = Claude(
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"hmm"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Done."}}}""");

        Assert.Equal("Done.", await TextOf(cli, Turn()));
    }

    [Fact]
    public async Task Codex_streams_the_new_part_of_each_update_only()
    {
        var cli = Codex(
            """{"type":"item.updated","item":{"item_type":"agent_message","text":"Hello"}}""",
            """{"type":"item.updated","item":{"item_type":"agent_message","text":"Hello world"}}""",
            """{"type":"item.completed","item":{"item_type":"agent_message","text":"Hello world"}}""");

        var events = await EventsOf(cli, Turn());

        Assert.Equal(["Hello", " world"], events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text));
    }

    [Fact]
    public async Task Codex_reads_the_item_shape_the_current_cli_actually_emits()
    {
        // Captured from codex-cli 0.145: the item names its kind "type", where older builds
        // used "item_type". Reading only the old name loses the answer entirely.
        var cli = Codex(
            """{"type":"thread.started","thread_id":"019ff515-c41c-7863-990c-3fd2a320d974"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"pong"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":13254,"output_tokens":5}}""");

        var events = await EventsOf(cli, Turn());

        Assert.Equal("pong", string.Concat(events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text)));
        Assert.Contains(events, e => e.Kind == AgentEventKind.SessionId && e.Text == "019ff515-c41c-7863-990c-3fd2a320d974");
    }

    [Fact]
    public async Task Codex_starts_a_second_message_from_scratch()
    {
        var cli = Codex(
            """{"type":"item.completed","item":{"item_type":"agent_message","text":"First."}}""",
            """{"type":"item.completed","item":{"item_type":"agent_message","text":"Second."}}""");

        Assert.Equal("First.Second.", await TextOf(cli, Turn()));
    }

    [Fact]
    public async Task Codex_handles_the_older_delta_events_without_repeating_the_message()
    {
        var cli = Codex(
            """{"msg":{"type":"agent_message_delta","delta":"Hel"}}""",
            """{"msg":{"type":"agent_message_delta","delta":"lo"}}""",
            """{"msg":{"type":"agent_message","message":"Hello"}}""");

        var events = await EventsOf(cli, Turn());

        Assert.Equal(["Hel", "lo"], events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text));
    }

    [Fact]
    public async Task Codex_shows_a_message_that_never_arrived_as_deltas()
    {
        var cli = Codex("""{"msg":{"type":"agent_message","message":"Hello"}}""");

        Assert.Equal("Hello", await TextOf(cli, Turn()));
    }

    [Fact]
    public async Task Claude_pairs_a_tool_call_with_the_result_that_finishes_it()
    {
        var cli = Claude(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_01","name":"Bash","input":{"command":"glab mr diff 42"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"the diff"}]}}""");

        var events = await EventsOf(cli, Turn());

        var call = Assert.Single(events, e => e.Kind == AgentEventKind.Tool);
        var finished = Assert.Single(events, e => e.Kind == AgentEventKind.ToolCompleted);
        Assert.Equal("toolu_01", call.ToolCallId);
        Assert.Equal("toolu_01", finished.ToolCallId);
    }

    [Fact]
    public async Task A_tool_result_adds_nothing_to_the_transcript_itself()
    {
        var cli = Claude(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"noisy output nobody asked for"}]}}""");

        var events = await EventsOf(cli, Turn());

        // The tool line is already in the transcript; the result only ends its timing.
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Text);
    }

    [Fact]
    public async Task Codex_pairs_a_command_with_the_event_that_completes_it()
    {
        var cli = Codex(
            """{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"glab mr diff 42"}}""",
            """{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"glab mr diff 42","exit_code":0}}""");

        var events = await EventsOf(cli, Turn());

        var call = Assert.Single(events, e => e.Kind == AgentEventKind.Tool);
        var finished = Assert.Single(events, e => e.Kind == AgentEventKind.ToolCompleted);
        Assert.Equal("item_1", call.ToolCallId);
        Assert.Equal("item_1", finished.ToolCallId);
    }

    [Fact]
    public async Task Codex_does_not_report_a_finished_command_twice()
    {
        var cli = Codex(
            """{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"ls"}}""",
            """{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"ls","exit_code":0}}""");

        var events = await EventsOf(cli, Turn());

        // The completion closes the existing line rather than opening a second one.
        Assert.Single(events, e => e.Kind == AgentEventKind.Tool);
    }

    /// <summary>Replays recorded CLI output instead of starting a process.</summary>
    private sealed class ScriptedRunner(string[] lines) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false));

        public async Task<int> StreamAsync(
            ProcessRequest request,
            Func<string, bool, CancellationToken, Task> onLine,
            CancellationToken ct = default)
        {
            foreach (var line in lines)
                await onLine(line, false, ct);

            return 0;
        }
    }

    /// <summary>No MCP servers in these transcripts, so nothing is ever probed.</summary>
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
