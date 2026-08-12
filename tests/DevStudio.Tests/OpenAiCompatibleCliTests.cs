using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.Processes;
using DevStudio.Infrastructure.Providers.OpenAi;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevStudio.Tests;

/// <summary>
/// The HTTP transport is the only one where this app is the agent rather than the client of one:
/// it advertises the tools, runs them and feeds the results back. These drive that loop against a
/// stubbed endpoint.
/// </summary>
public class OpenAiCompatibleCliTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devstudio-llama-" + Guid.NewGuid().ToString("n"));
    private readonly StubEndpoint _endpoint = new();
    private readonly ConversationStore _conversations = new();

    public OpenAiCompatibleCliTests() => Directory.CreateDirectory(_workspace);

    private OpenAiCompatibleCli Create(int maxToolCalls = 25, bool stream = true) =>
        new(new CliProvider
            {
                Name = "llama.cpp",
                Transport = CliTransport.OpenAiCompatible,
                BaseUrl = "http://localhost:8080/v1",
                MaxToolCalls = maxToolCalls,
                Models = ["local-model"],
                Stream = stream,
            },
            _endpoint,
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            _conversations,
            NullLogger.Instance);

    private TurnRequest Turn(PermissionMode mode = PermissionMode.AcceptEdits, string? resume = null) => new()
    {
        Prompt = "do the thing",
        WorkingDirectory = _workspace,
        PermissionMode = mode,
        ResumeSessionId = resume,
    };

    private async Task<List<AgentEvent>> RunAsync(TurnRequest request, int maxToolCalls = 25, bool stream = true)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in Create(maxToolCalls, stream).RunTurnAsync(request, CancellationToken.None))
            events.Add(evt);

        return events;
    }

    private static string SystemPromptOf(string requestBody) =>
        JsonNode.Parse(requestBody)!["messages"]!.AsArray()
            .First(m => m!["role"]!.GetValue<string>() == "system")!["content"]!.GetValue<string>();

    /// <summary>An answer with no tool calls, streamed a piece at a time.</summary>
    private static string Says(params string[] pieces)
    {
        var body = new StringBuilder();
        foreach (var piece in pieces)
        {
            body.AppendLine($"data: {new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["delta"] = new JsonObject { ["content"] = piece },
                }),
            }.ToJsonString()}");
        }

        body.AppendLine("data: [DONE]");
        return body.ToString();
    }

    /// <summary>A tool call, with its arguments split across chunks the way a real stream sends them.</summary>
    private static string Calls(string id, string name, params string[] argumentFragments)
    {
        var body = new StringBuilder();
        body.AppendLine($"data: {Fragment(new JsonObject
        {
            ["index"] = 0,
            ["id"] = id,
            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = string.Empty },
        })}");

        foreach (var fragment in argumentFragments)
        {
            body.AppendLine($"data: {Fragment(new JsonObject
            {
                ["index"] = 0,
                ["function"] = new JsonObject { ["arguments"] = fragment },
            })}");
        }

        body.AppendLine("data: [DONE]");
        return body.ToString();

        static string Fragment(JsonObject call) => new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(call) },
            }),
        }.ToJsonString();
    }

    private static JsonArray ToolsOf(string requestBody) =>
        JsonNode.Parse(requestBody)!["tools"]!.AsArray();

    private static List<string> ToolNames(string requestBody) =>
        [.. ToolsOf(requestBody).Select(t => t!["function"]!["name"]!.GetValue<string>())];

    [Fact]
    public async Task Text_is_streamed_piece_by_piece()
    {
        _endpoint.Responses.Enqueue(Says("Hello ", "world"));

        var events = await RunAsync(Turn());

        Assert.Equal(["Hello ", "world"], events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text));
        Assert.Contains(events, e => e.Kind == AgentEventKind.Result && e.Text == "Hello world");
    }

    [Fact]
    public async Task A_tool_call_is_run_and_its_result_handed_back_to_the_model()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "readme.md"), "the contents");

        _endpoint.Responses.Enqueue(Calls("call-1", "read_file", "{\"path\":", "\"readme.md\"}"));
        _endpoint.Responses.Enqueue(Says("It says: the contents"));

        var events = await RunAsync(Turn());

        // The second request has to carry both the assistant's call and the tool's answer, or the
        // model has no idea what it just learned.
        var followUp = JsonNode.Parse(_endpoint.Requests[1])!["messages"]!.AsArray();
        var toolMessage = followUp.Last(m => m!["role"]!.GetValue<string>() == "tool")!;

        Assert.Equal("call-1", toolMessage["tool_call_id"]!.GetValue<string>());
        Assert.Equal("the contents", toolMessage["content"]!.GetValue<string>());
        Assert.Contains(events, e => e.Kind == AgentEventKind.Tool && e.ToolName == "read_file");
        Assert.Contains(events, e => e.Kind == AgentEventKind.Result && e.Text == "It says: the contents");
    }

    [Fact]
    public async Task A_tool_this_app_runs_itself_is_timed_exactly()
    {
        _endpoint.Responses.Enqueue(Calls("call-1", "list_files", "{}"));
        _endpoint.Responses.Enqueue(Says("nothing there"));

        var events = await RunAsync(Turn());

        var finished = Assert.Single(events, e => e.Kind == AgentEventKind.ToolCompleted);
        Assert.Equal("call-1", finished.ToolCallId);
        Assert.NotNull(finished.DurationMs);
        Assert.True(finished.DurationMs >= 0);
    }

    [Fact]
    public async Task A_read_only_session_is_offered_no_way_to_change_anything()
    {
        _endpoint.Responses.Enqueue(Says("nothing to do"));

        await RunAsync(Turn(PermissionMode.Plan));

        var names = ToolNames(_endpoint.Requests[0]);
        Assert.Equal(["read_file", "list_files"], names);
    }

    [Fact]
    public async Task Editing_adds_writing_but_still_no_shell()
    {
        _endpoint.Responses.Enqueue(Says("ok"));

        await RunAsync(Turn(PermissionMode.AcceptEdits));

        var names = ToolNames(_endpoint.Requests[0]);
        Assert.Contains("write_file", names);
        Assert.DoesNotContain("run_command", names);
    }

    [Fact]
    public async Task Only_an_unrestricted_session_gets_a_shell()
    {
        _endpoint.Responses.Enqueue(Says("ok"));

        await RunAsync(Turn(PermissionMode.Unrestricted));

        Assert.Contains("run_command", ToolNames(_endpoint.Requests[0]));
    }

    [Fact]
    public async Task A_tool_the_mode_forbids_is_refused_rather_than_run()
    {
        // A model can ask for anything; the answer has to be a refusal it can read, not a crash.
        _endpoint.Responses.Enqueue(Calls("call-1", "write_file", "{\"path\":\"x.txt\",\"content\":\"hi\"}"));
        _endpoint.Responses.Enqueue(Says("understood"));

        await RunAsync(Turn(PermissionMode.Plan));

        var toolMessage = JsonNode.Parse(_endpoint.Requests[1])!["messages"]!.AsArray()
            .Last(m => m!["role"]!.GetValue<string>() == "tool")!;

        Assert.Contains("not allowed", toolMessage["content"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_workspace, "x.txt")));
    }

    [Fact]
    public async Task A_path_outside_the_workspace_comes_back_as_an_error_the_model_can_read()
    {
        _endpoint.Responses.Enqueue(Calls("call-1", "read_file", "{\"path\":\"../../secrets.txt\"}"));
        _endpoint.Responses.Enqueue(Says("sorry"));

        await RunAsync(Turn());

        var toolMessage = JsonNode.Parse(_endpoint.Requests[1])!["messages"]!.AsArray()
            .Last(m => m!["role"]!.GetValue<string>() == "tool")!;

        Assert.Contains("outside the workspace", toolMessage["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Malformed_arguments_are_reported_back_instead_of_ending_the_turn()
    {
        _endpoint.Responses.Enqueue(Calls("call-1", "read_file", "{not json"));
        _endpoint.Responses.Enqueue(Says("let me try again"));

        var events = await RunAsync(Turn());

        Assert.Contains(events, e => e.Kind == AgentEventKind.Result && e.Text == "let me try again");
    }

    [Fact]
    public async Task A_model_that_never_stops_calling_tools_is_cut_off()
    {
        for (var i = 0; i < 5; i++)
            _endpoint.Responses.Enqueue(Calls($"call-{i}", "list_files", "{}"));

        var events = await RunAsync(Turn(), maxToolCalls: 3);

        var error = Assert.Single(events, e => e.Kind == AgentEventKind.Error);
        Assert.Contains("Stopped after 3 tool calls", error.Text);
        Assert.Equal(3, _endpoint.Requests.Count);
    }

    [Fact]
    public async Task A_second_turn_continues_the_same_conversation()
    {
        _endpoint.Responses.Enqueue(Says("first answer"));
        var first = await RunAsync(Turn());
        var sessionId = first.Single(e => e.Kind == AgentEventKind.SessionId).Text;

        _endpoint.Responses.Enqueue(Says("second answer"));
        await RunAsync(Turn(resume: sessionId));

        // system preamble, the first exchange, then this turn's question.
        var messages = JsonNode.Parse(_endpoint.Requests[1])!["messages"]!.AsArray();
        Assert.Equal(4, messages.Count);
        Assert.Equal("first answer", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_session_the_store_has_forgotten_starts_over_rather_than_failing()
    {
        _endpoint.Responses.Enqueue(Says("fresh start"));

        var events = await RunAsync(Turn(resume: "a-session-from-before-a-restart"));

        // Just the preamble and the question: nothing was carried over from the lost session.
        var messages = JsonNode.Parse(_endpoint.Requests[0])!["messages"]!.AsArray();
        Assert.Equal(["system", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Contains(events, e => e.Kind == AgentEventKind.Result);
    }

    [Fact]
    public async Task An_endpoint_that_refuses_the_request_says_so_in_the_transcript()
    {
        _endpoint.Status = HttpStatusCode.NotFound;
        _endpoint.Responses.Enqueue("no such model");

        var events = await RunAsync(Turn());

        var error = Assert.Single(events, e => e.Kind == AgentEventKind.Error);
        Assert.Contains("404", error.Text);
    }

    [Fact]
    public async Task The_model_is_told_it_has_tools_and_where_it_is_working()
    {
        _endpoint.Responses.Enqueue(Says("ok"));

        await RunAsync(Turn(PermissionMode.AcceptEdits));

        // Without this a local model answers that it cannot see a filesystem and stops there.
        var system = SystemPromptOf(_endpoint.Requests[0]);
        Assert.Contains(_workspace, system);
        Assert.Contains("read_file", system);
        Assert.Contains("write_file", system);
    }

    [Fact]
    public async Task The_preamble_only_names_the_tools_the_mode_actually_allows()
    {
        _endpoint.Responses.Enqueue(Says("ok"));

        await RunAsync(Turn(PermissionMode.Plan));

        var system = SystemPromptOf(_endpoint.Requests[0]);
        Assert.Contains("read_file", system);
        Assert.DoesNotContain("run_command", system);
    }

    [Fact]
    public async Task The_agents_own_instructions_survive_the_preamble()
    {
        _endpoint.Responses.Enqueue(Says("ok"));

        await foreach (var _ in Create().RunTurnAsync(
            Turn() with { SystemPrompt = "Always answer in French." }, CancellationToken.None))
        {
        }

        var system = SystemPromptOf(_endpoint.Requests[0]);
        Assert.Contains("read_file", system);
        Assert.Contains("Always answer in French.", system);
    }

    [Fact]
    public async Task A_provider_with_streaming_off_asks_for_a_whole_response()
    {
        // Some servers only report tool calls when they are not streaming.
        _endpoint.Responses.Enqueue(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["content"] = "the whole answer",
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["id"] = "call-1",
                        ["function"] = new JsonObject { ["name"] = "list_files", ["arguments"] = "{}" },
                    }),
                },
            }),
        }.ToJsonString());
        _endpoint.Responses.Enqueue(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["content"] = "done" },
            }),
        }.ToJsonString());

        var events = await RunAsync(Turn(), stream: false);

        Assert.False(JsonNode.Parse(_endpoint.Requests[0])!["stream"]!.GetValue<bool>());
        Assert.Contains(events, e => e.Kind == AgentEventKind.Text && e.Text == "the whole answer");
        Assert.Contains(events, e => e.Kind == AgentEventKind.Tool && e.ToolName == "list_files");
        Assert.Contains(events, e => e.Kind == AgentEventKind.Result && e.Text == "done");
    }

    /// <summary>Answers each request with the next canned body, and records what it was asked.</summary>
    private sealed class StubEndpoint : IHttpClientFactory
    {
        public Queue<string> Responses { get; } = new();
        public List<string> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(StubEndpoint endpoint) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method == HttpMethod.Get)
                    return new HttpResponseMessage(endpoint.Status);

                endpoint.Requests.Add(await request.Content!.ReadAsStringAsync(ct));

                var body = endpoint.Responses.Count > 0 ? endpoint.Responses.Dequeue() : "data: [DONE]\n";
                return new HttpResponseMessage(endpoint.Status) { Content = new StringContent(body) };
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);

        GC.SuppressFinalize(this);
    }
}
