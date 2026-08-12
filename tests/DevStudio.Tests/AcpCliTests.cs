using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.Providers.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevStudio.Tests;

/// <summary>
/// Drives the ACP client against a scripted agent: no process, just the JSON-RPC both sides would
/// have exchanged over stdio.
/// </summary>
public class AcpCliTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devstudio-acp-" + Guid.NewGuid().ToString("n"));
    private readonly ScriptedAcpConnection _connection = new();

    public AcpCliTests() => Directory.CreateDirectory(_workspace);

    private AcpCli Create() =>
        new(new CliProvider { Name = "Scripted agent", Executable = "agent", Transport = CliTransport.Acp },
            new StubFactory(_connection),
            new OrchestratorOptions { HomePath = _workspace },
            NullLogger.Instance);

    private TurnRequest Turn(PermissionMode mode = PermissionMode.AcceptEdits, string? resume = null) => new()
    {
        Prompt = "do the thing",
        WorkingDirectory = _workspace,
        PermissionMode = mode,
        ResumeSessionId = resume,
    };

    /// <summary>
    /// Answers the client the way an agent would: initialize, hand out a session, then whatever the
    /// test wants to happen during the prompt.
    /// </summary>
    private void Script(bool canLoad = true, Action<ScriptedAcpConnection>? duringPrompt = null, bool remoteMcp = false)
    {
        _connection.OnWrite = (line, connection) =>
        {
            var message = JsonNode.Parse(line)!.AsObject();
            var method = message["method"]?.GetValue<string>();
            var id = message["id"]?.GetValue<int>();

            switch (method)
            {
                case "initialize":
                    connection.Reply(Result(id!.Value, new JsonObject
                    {
                        ["protocolVersion"] = 1,
                        ["agentCapabilities"] = new JsonObject
                        {
                            ["loadSession"] = canLoad,
                            ["mcpCapabilities"] = new JsonObject
                            {
                                ["http"] = remoteMcp,
                                ["sse"] = remoteMcp,
                            },
                        },
                    }));
                    break;

                case "session/new":
                    connection.Reply(Result(id!.Value, new JsonObject { ["sessionId"] = "sess-1" }));
                    break;

                case "session/load":
                    connection.Reply(Result(id!.Value, new JsonObject()));
                    break;

                case "session/prompt":
                    duringPrompt?.Invoke(connection);
                    connection.Reply(Result(id!.Value, new JsonObject { ["stopReason"] = "end_turn" }));
                    break;
            }

            return Task.CompletedTask;
        };
    }

    private static string Result(int id, JsonObject result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Update(JsonObject update) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = new JsonObject { ["sessionId"] = "sess-1", ["update"] = update },
        }.ToJsonString();

    private static string Request(int id, string method, JsonObject parameters) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        }.ToJsonString();

    private async Task<List<AgentEvent>> RunAsync(TurnRequest request)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in Create().RunTurnAsync(request, CancellationToken.None))
            events.Add(evt);

        return events;
    }

    private static IEnumerable<JsonObject> Sent(ScriptedAcpConnection connection) =>
        connection.Written.Select(line => JsonNode.Parse(line)!.AsObject());

    [Fact]
    public async Task Message_chunks_reach_the_transcript_as_they_arrive()
    {
        Script(duringPrompt: c =>
        {
            c.Reply(Update(new JsonObject
            {
                ["sessionUpdate"] = "agent_message_chunk",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Hello " },
            }));
            c.Reply(Update(new JsonObject
            {
                ["sessionUpdate"] = "agent_message_chunk",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "world" },
            }));
        });

        var events = await RunAsync(Turn());

        Assert.Equal(["Hello ", "world"], events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text));
        Assert.Contains(events, e => e.Kind == AgentEventKind.SessionId && e.Text == "sess-1");
    }

    [Fact]
    public async Task Thinking_is_logged_rather_than_shown_as_the_answer()
    {
        Script(duringPrompt: c => c.Reply(Update(new JsonObject
        {
            ["sessionUpdate"] = "agent_thought_chunk",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = "considering" },
        })));

        var events = await RunAsync(Turn());

        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Text);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Log && e.Text == "considering");
    }

    [Fact]
    public async Task A_tool_call_is_reported_with_its_kind()
    {
        Script(duringPrompt: c => c.Reply(Update(new JsonObject
        {
            ["sessionUpdate"] = "tool_call",
            ["toolCallId"] = "call-1",
            ["title"] = "Read config.json",
            ["kind"] = "read",
        })));

        var events = await RunAsync(Turn());

        var tool = Assert.Single(events, e => e.Kind == AgentEventKind.Tool);
        Assert.Equal("Read config.json", tool.Text);
        Assert.Equal("read", tool.ToolName);
    }

    [Fact]
    public async Task A_tool_call_is_closed_by_the_update_that_finishes_it()
    {
        Script(duringPrompt: c =>
        {
            c.Reply(Update(new JsonObject
            {
                ["sessionUpdate"] = "tool_call",
                ["toolCallId"] = "call-1",
                ["title"] = "Read config.json",
                ["kind"] = "read",
            }));
            c.Reply(Update(new JsonObject
            {
                ["sessionUpdate"] = "tool_call_update",
                ["toolCallId"] = "call-1",
                ["status"] = "in_progress",
            }));
            c.Reply(Update(new JsonObject
            {
                ["sessionUpdate"] = "tool_call_update",
                ["toolCallId"] = "call-1",
                ["status"] = "completed",
            }));
        });

        var events = await RunAsync(Turn());

        Assert.Equal("call-1", Assert.Single(events, e => e.Kind == AgentEventKind.Tool).ToolCallId);

        // Only the final status ends the timing; in_progress says nothing new.
        Assert.Equal("call-1", Assert.Single(events, e => e.Kind == AgentEventKind.ToolCompleted).ToolCallId);
    }

    [Fact]
    public async Task An_editing_session_allows_what_the_agent_asks_for()
    {
        Script(duringPrompt: c => c.Reply(Request(99, "session/request_permission", new JsonObject
        {
            ["sessionId"] = "sess-1",
            ["toolCall"] = new JsonObject { ["title"] = "Write a file", ["kind"] = "edit" },
            ["options"] = new JsonArray(
                new JsonObject { ["optionId"] = "yes", ["name"] = "Allow", ["kind"] = "allow_once" },
                new JsonObject { ["optionId"] = "no", ["name"] = "Reject", ["kind"] = "reject_once" }),
        })));

        var events = await RunAsync(Turn(PermissionMode.AcceptEdits));

        var answer = Sent(_connection).Single(m => m["id"]?.GetValue<int>() == 99);
        Assert.Equal("selected", answer["result"]!["outcome"]!["outcome"]!.GetValue<string>());
        Assert.Equal("yes", answer["result"]!["outcome"]!["optionId"]!.GetValue<string>());
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.PermissionDenied);
    }

    [Fact]
    public async Task A_read_only_session_refuses_and_records_the_refusal()
    {
        Script(duringPrompt: c => c.Reply(Request(99, "session/request_permission", new JsonObject
        {
            ["sessionId"] = "sess-1",
            ["toolCall"] = new JsonObject { ["title"] = "Write a file", ["kind"] = "edit" },
            ["options"] = new JsonArray(
                new JsonObject { ["optionId"] = "yes", ["name"] = "Allow", ["kind"] = "allow_once" },
                new JsonObject { ["optionId"] = "no", ["name"] = "Reject", ["kind"] = "reject_once" }),
        })));

        var events = await RunAsync(Turn(PermissionMode.Plan));

        var answer = Sent(_connection).Single(m => m["id"]?.GetValue<int>() == 99);
        Assert.Equal("no", answer["result"]!["outcome"]!["optionId"]!.GetValue<string>());

        var denied = Assert.Single(events, e => e.Kind == AgentEventKind.PermissionDenied);
        Assert.Equal("Write a file", denied.Text);
    }

    [Fact]
    public async Task The_client_serves_file_reads_from_the_workspace()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "notes.txt"), "one\ntwo\nthree");

        Script(duringPrompt: c => c.Reply(Request(50, "fs/read_text_file", new JsonObject
        {
            ["sessionId"] = "sess-1",
            ["path"] = "notes.txt",
            ["line"] = 2,
            ["limit"] = 2,
        })));

        await RunAsync(Turn());

        var answer = Sent(_connection).Single(m => m["id"]?.GetValue<int>() == 50);
        Assert.Equal("two\nthree", answer["result"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_path_outside_the_workspace_is_refused()
    {
        Script(duringPrompt: c => c.Reply(Request(51, "fs/read_text_file", new JsonObject
        {
            ["sessionId"] = "sess-1",
            ["path"] = "../escape.txt",
        })));

        await RunAsync(Turn());

        var answer = Sent(_connection).Single(m => m["id"]?.GetValue<int>() == 51);
        Assert.Contains("outside the workspace", answer["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_read_only_session_will_not_write_a_file_for_the_agent_either()
    {
        Script(duringPrompt: c => c.Reply(Request(52, "fs/write_text_file", new JsonObject
        {
            ["sessionId"] = "sess-1",
            ["path"] = "new.txt",
            ["content"] = "nope",
        })));

        await RunAsync(Turn(PermissionMode.Plan));

        var answer = Sent(_connection).Single(m => m["id"]?.GetValue<int>() == 52);
        Assert.Contains("read-only", answer["error"]!["message"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_workspace, "new.txt")));
    }

    [Fact]
    public async Task An_agent_that_can_load_resumes_the_stored_session()
    {
        Script(canLoad: true);

        await RunAsync(Turn(resume: "sess-earlier"));

        var methods = Sent(_connection).Select(m => m["method"]?.GetValue<string>()).ToList();
        Assert.Contains("session/load", methods);
        Assert.DoesNotContain("session/new", methods);
    }

    [Fact]
    public async Task An_agent_that_cannot_load_starts_a_new_session_instead()
    {
        Script(canLoad: false);

        var events = await RunAsync(Turn(resume: "sess-earlier"));

        var methods = Sent(_connection).Select(m => m["method"]?.GetValue<string>()).ToList();
        Assert.Contains("session/new", methods);
        Assert.DoesNotContain("session/load", methods);

        // The transcript has to learn the new id, or the next turn resumes one that never existed.
        Assert.Contains(events, e => e.Kind == AgentEventKind.SessionId && e.Text == "sess-1");
    }

    [Fact]
    public async Task An_agent_that_dies_mid_turn_is_reported_rather_than_hanging()
    {
        _connection.OnWrite = (_, connection) =>
        {
            connection.Complete();
            return Task.CompletedTask;
        };

        var events = await RunAsync(Turn());

        Assert.Contains(events, e => e.Kind == AgentEventKind.Error);
    }

    /// <summary>Writes the config the workspace would have written before the turn started.</summary>
    private async Task WriteMcpConfigAsync(JsonObject servers) =>
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, ".mcp.json"),
            new JsonObject { ["mcpServers"] = servers }.ToJsonString());

    private JsonArray McpServersSentWith(string method) =>
        Sent(_connection).Single(m => m["method"]?.GetValue<string>() == method)["params"]!["mcpServers"]!.AsArray();

    [Fact]
    public async Task Configured_stdio_servers_are_handed_to_the_new_session()
    {
        await WriteMcpConfigAsync(new JsonObject
        {
            ["github"] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = "npx",
                ["args"] = new JsonArray("-y", "@modelcontextprotocol/server-github"),
                ["env"] = new JsonObject { ["GITHUB_TOKEN"] = "secret" },
            },
        });

        Script();
        await RunAsync(Turn());

        var server = Assert.Single(McpServersSentWith("session/new"))!.AsObject();
        Assert.Equal("github", server["name"]!.GetValue<string>());
        Assert.Equal("npx", server["command"]!.GetValue<string>());
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], server["args"]!.AsArray().Select(a => a!.GetValue<string>()));

        // ACP takes environment as name/value pairs, not as an object like the CLI config file.
        var environment = Assert.Single(server["env"]!.AsArray())!.AsObject();
        Assert.Equal("GITHUB_TOKEN", environment["name"]!.GetValue<string>());
        Assert.Equal("secret", environment["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_resumed_session_is_given_the_same_servers()
    {
        await WriteMcpConfigAsync(new JsonObject
        {
            ["local"] = new JsonObject { ["type"] = "stdio", ["command"] = "server", ["args"] = new JsonArray() },
        });

        Script(canLoad: true);
        await RunAsync(Turn(resume: "sess-earlier"));

        Assert.Single(McpServersSentWith("session/load"));
    }

    [Fact]
    public async Task A_remote_server_is_passed_when_the_agent_can_take_one()
    {
        await WriteMcpConfigAsync(new JsonObject
        {
            ["docs"] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = "https://example.com/mcp",
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer abc" },
            },
        });

        Script(remoteMcp: true);
        await RunAsync(Turn());

        var server = Assert.Single(McpServersSentWith("session/new"))!.AsObject();
        Assert.Equal("http", server["type"]!.GetValue<string>());
        Assert.Equal("https://example.com/mcp", server["url"]!.GetValue<string>());

        var header = Assert.Single(server["headers"]!.AsArray())!.AsObject();
        Assert.Equal("Authorization", header["name"]!.GetValue<string>());
        Assert.Equal("Bearer abc", header["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_remote_server_is_left_out_and_said_so_when_the_agent_cannot()
    {
        await WriteMcpConfigAsync(new JsonObject
        {
            ["docs"] = new JsonObject { ["type"] = "http", ["url"] = "https://example.com/mcp" },
            ["local"] = new JsonObject { ["type"] = "stdio", ["command"] = "server", ["args"] = new JsonArray() },
        });

        Script(remoteMcp: false);
        var events = await RunAsync(Turn());

        // Sending one the agent cannot take would fail the whole session, so the rest still go.
        var server = Assert.Single(McpServersSentWith("session/new"))!.AsObject();
        Assert.Equal("local", server["name"]!.GetValue<string>());
        Assert.Contains(events, e => e.Kind == AgentEventKind.Log && e.Text.Contains("docs"));
    }

    [Fact]
    public async Task A_session_with_no_servers_configured_sends_an_empty_list()
    {
        Script();
        await RunAsync(Turn());

        Assert.Empty(McpServersSentWith("session/new"));
    }

    [Fact]
    public async Task An_unreadable_config_does_not_take_the_turn_down_with_it()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, ".mcp.json"), "{ not json");

        Script(duringPrompt: c => c.Reply(Update(new JsonObject
        {
            ["sessionUpdate"] = "agent_message_chunk",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = "still here" },
        })));

        var events = await RunAsync(Turn());

        Assert.Empty(McpServersSentWith("session/new"));
        Assert.Contains(events, e => e.Kind == AgentEventKind.Text && e.Text == "still here");
    }

    private sealed class StubFactory(IAcpConnection connection) : IAcpConnectionFactory
    {
        public Task<IAcpConnection> ConnectAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken ct) => Task.FromResult(connection);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);

        GC.SuppressFinalize(this);
    }
}
