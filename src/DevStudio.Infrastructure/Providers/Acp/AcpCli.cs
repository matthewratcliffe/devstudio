using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Providers.Acp;

/// <summary>
/// Drives an agent that speaks the Agent Client Protocol: JSON-RPC over the agent's stdio, with
/// the agent calling back for permission and for file access. Unlike the CLI adapters, nothing here
/// has to guess at an output format — the protocol names what each update is, and text already
/// arrives in chunks, so the transcript streams without any coaxing.
/// </summary>
public sealed class AcpCli : IProviderCli
{
    /// <summary>The revision of the protocol this client implements.</summary>
    private const int ProtocolVersion = 1;

    private readonly CliProvider _definition;
    private readonly IAcpConnectionFactory _connections;
    private readonly OrchestratorOptions _options;
    private readonly ILogger _logger;
    private readonly WorkspacePathPolicy _policy;

    public AcpCli(
        CliProvider definition,
        IAcpConnectionFactory connections,
        OrchestratorOptions options,
        ILogger logger,
        WorkspacePathPolicy? policy = null)
    {
        _definition = definition;
        _connections = connections;
        _options = options;
        _logger = logger;
        _policy = policy ?? new WorkspacePathPolicy();
    }

    public AiProvider Provider => AiProvider.Custom;
    public string DisplayName => _definition.Name;
    public string? DefinitionId => _definition.Id;

    // The agent owns its own credentials, exactly as a CLI does; there is nothing to sign into here.
    public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
        (_definition.Executable, [.. ClaudeCli.SplitArguments(_definition.LoginArguments)]);

    public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() =>
        (_definition.Executable, [.. ClaudeCli.SplitArguments(_definition.LogoutArguments)]);

    /// <summary>
    /// Nothing is probed: starting the agent just to ask would cost as much as running the turn,
    /// and an agent that cannot start says so on the first turn anyway.
    /// </summary>
    public Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default) =>
        Task.FromResult(ProviderAuthStatus.Unknown(AiProvider.Custom, $"{_definition.Name} manages its own sign-in."));

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        TurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var events = Channel.CreateUnbounded<AgentEvent>();

        var pump = Task.Run(async () =>
        {
            IAcpConnection? connection = null;
            try
            {
                connection = await _connections.ConnectAsync(
                    _definition.Executable,
                    [.. ClaudeCli.SplitArguments(_definition.AcpArguments)],
                    request.WorkingDirectory,
                    BuildEnvironment(request),
                    ct);

                await new Turn(connection, request, _definition, events.Writer, _logger, _policy).RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Stopping a turn is the session manager's business, not a transcript error.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ACP turn failed for {Agent}", _definition.Name);
                await events.Writer.WriteAsync(AgentEvent.Error(ex.Message), CancellationToken.None);
            }
            finally
            {
                if (connection is not null)
                    await connection.DisposeAsync();

                events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var evt in events.Reader.ReadAllAsync(CancellationToken.None))
            yield return evt;

        await pump;
    }

    private Dictionary<string, string> BuildEnvironment(TurnRequest request)
    {
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = request.HomeDirectory ?? _options.HomePath,
            ["NO_COLOR"] = "1",
        };

        foreach (var pair in _definition.Environment)
            environment[pair.Key] = pair.Value;

        foreach (var pair in request.Environment)
            environment[pair.Key] = pair.Value;

        return environment;
    }

    /// <summary>
    /// One prompt, from initialize to the stop reason. Written as a class because the exchange is
    /// stateful: requests have to be matched to their answers while the agent's own requests are
    /// answered in between.
    /// </summary>
    private sealed class Turn(
        IAcpConnection connection,
        TurnRequest request,
        CliProvider definition,
        ChannelWriter<AgentEvent> events,
        ILogger logger,
        WorkspacePathPolicy policy)
    {
        private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = [];
        private readonly WorkspacePathPolicy _policy = policy;
        private int _nextId;

        public async Task RunAsync(CancellationToken ct)
        {
            // The agent writes on its own schedule, so reading runs alongside the exchange below
            // rather than after it.
            var reader = Task.Run(() => ReadAsync(ct), CancellationToken.None);

            try
            {
                var initialize = await CallAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["clientCapabilities"] = new JsonObject
                    {
                        ["fs"] = new JsonObject
                        {
                            ["readTextFile"] = true,
                            ["writeTextFile"] = true,
                        },
                        // Not implemented: the agent runs its own commands, and offering a terminal
                        // we do not have would only fail later.
                        ["terminal"] = false,
                    },
                }, ct);

                var canLoad = initialize?["agentCapabilities"]?["loadSession"]?.GetValue<bool>() ?? false;
                var mcpServers = await CollectMcpServersAsync(initialize, ct);
                var sessionId = await OpenSessionAsync(canLoad, mcpServers, ct);

                await events.WriteAsync(new AgentEvent(AgentEventKind.SessionId, sessionId), ct);

                var result = await CallAsync("session/prompt", new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["prompt"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = FullPrompt(),
                    }),
                }, ct);

                var stopReason = result?["stopReason"]?.GetValue<string>() ?? "end_turn";
                if (stopReason == "refusal")
                    await events.WriteAsync(AgentEvent.Error("The agent refused to answer."), ct);
                else
                    await events.WriteAsync(new AgentEvent(AgentEventKind.Result, string.Empty), ct);
            }
            finally
            {
                await connection.DisposeAsync();
                await reader;
            }
        }

        /// <summary>
        /// Resumes when the agent supports it, and otherwise starts over. Either way the id that
        /// comes back is the one the transcript stores, so a session that loses its history still
        /// has a valid handle for the next turn.
        /// </summary>
        private async Task<string> OpenSessionAsync(bool canLoad, JsonArray mcpServers, CancellationToken ct)
        {
            if (canLoad && !string.IsNullOrWhiteSpace(request.ResumeSessionId))
            {
                await CallAsync("session/load", new JsonObject
                {
                    ["sessionId"] = request.ResumeSessionId!,
                    ["cwd"] = request.WorkingDirectory,
                    ["mcpServers"] = mcpServers.DeepClone(),
                }, ct);

                return request.ResumeSessionId!;
            }

            var created = await CallAsync("session/new", new JsonObject
            {
                ["cwd"] = request.WorkingDirectory,
                ["mcpServers"] = mcpServers.DeepClone(),
            }, ct);

            return created?["sessionId"]?.GetValue<string>()
                   ?? throw new InvalidOperationException("The agent did not return a session id.");
        }

        /// <summary>ACP has no separate system prompt, so it rides at the top of the first message.</summary>
        private string FullPrompt() =>
            string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? request.Prompt
                : $"{request.SystemPrompt}\n\n---\n\n{request.Prompt}";

        /// <summary>
        /// The MCP servers this turn should have, taken from the .mcp.json the workspace has already
        /// written. That file is the right source rather than the store: it is filtered to this
        /// agent's servers and its OAuth tokens were refreshed when it was written, so the agent
        /// starts with credentials that are still good.
        /// </summary>
        private async Task<JsonArray> CollectMcpServersAsync(JsonNode? initialize, CancellationToken ct)
        {
            var configPath = Path.Combine(request.WorkingDirectory, ".mcp.json");
            if (!File.Exists(configPath))
                return [];

            JsonObject? configured;
            try
            {
                var text = await File.ReadAllTextAsync(configPath, ct);
                configured = JsonNode.Parse(text)?["mcpServers"] as JsonObject;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read the MCP config for {Agent}", definition.Name);
                return [];
            }

            if (configured is null)
                return [];

            // Remote servers are an optional part of the protocol; an agent that cannot take them
            // would reject the whole session/new, so they are dropped with a note instead.
            var capabilities = initialize?["agentCapabilities"]?["mcpCapabilities"];
            var canHttp = capabilities?["http"]?.GetValue<bool>() ?? false;
            var canSse = capabilities?["sse"]?.GetValue<bool>() ?? false;

            var servers = new JsonArray();

            foreach (var (name, entry) in configured)
            {
                if (entry is not JsonObject server)
                    continue;

                var type = server["type"]?.GetValue<string>() ?? "stdio";

                switch (type)
                {
                    case "stdio":
                        servers.Add(Stdio(name, server));
                        break;

                    case "http" when canHttp:
                    case "sse" when canSse:
                        servers.Add(Remote(name, type, server));
                        break;

                    default:
                        await events.WriteAsync(AgentEvent.Log(
                            $"{definition.Name} does not support {type} MCP servers, so '{name}' was left out."), ct);
                        break;
                }
            }

            return servers;
        }

        /// <summary>
        /// ACP names the pieces separately and takes environment as a list of name/value pairs,
        /// where the CLIs' config file keeps it as an object.
        /// </summary>
        private static JsonObject Stdio(string name, JsonObject server)
        {
            var entry = new JsonObject
            {
                ["name"] = name,
                ["command"] = server["command"]?.GetValue<string>() ?? string.Empty,
                ["args"] = (server["args"] as JsonArray)?.DeepClone() ?? new JsonArray(),
                ["env"] = Pairs(server["env"] as JsonObject),
            };

            return entry;
        }

        private static JsonObject Remote(string name, string type, JsonObject server) => new()
        {
            ["type"] = type,
            ["name"] = name,
            ["url"] = server["url"]?.GetValue<string>() ?? string.Empty,
            ["headers"] = Pairs(server["headers"] as JsonObject),
        };

        private static JsonArray Pairs(JsonObject? values)
        {
            var pairs = new JsonArray();
            if (values is null)
                return pairs;

            foreach (var (key, value) in values)
            {
                pairs.Add(new JsonObject
                {
                    ["name"] = key,
                    ["value"] = value?.GetValue<string>() ?? string.Empty,
                });
            }

            return pairs;
        }

        // -- transport ---------------------------------------------------------

        private async Task<JsonNode?> CallAsync(string method, JsonObject parameters, CancellationToken ct)
        {
            var id = ++_nextId;
            var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;

            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            }, ct);

            using var registration = ct.Register(() => completion.TrySetCanceled(ct));
            return await completion.Task;
        }

        private Task SendAsync(JsonObject message, CancellationToken ct) =>
            connection.WriteLineAsync(message.ToJsonString(), ct);

        private async Task ReadAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var line in connection.ReadLinesAsync(ct))
                {
                    JsonObject? message;
                    try
                    {
                        message = JsonNode.Parse(line) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        // Agents occasionally print a banner before the protocol starts.
                        logger.LogDebug("Ignoring non-JSON line from {Agent}: {Line}", definition.Name, line);
                        continue;
                    }

                    if (message is not null)
                        await HandleAsync(message, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                // Nothing else is coming, so anything still waiting would wait for ever.
                foreach (var pending in _pending.Values)
                    pending.TrySetException(new InvalidOperationException("The agent closed the connection."));

                _pending.Clear();
            }
        }

        private async Task HandleAsync(JsonObject message, CancellationToken ct)
        {
            // An answer to something we asked.
            if (message["id"] is { } id && message["method"] is null)
            {
                if (id.GetValueKind() != JsonValueKind.Number || !_pending.Remove(id.GetValue<int>(), out var pending))
                    return;

                if (message["error"] is { } error)
                {
                    pending.TrySetException(new InvalidOperationException(
                        error["message"]?.GetValue<string>() ?? "The agent reported an error."));
                }
                else
                {
                    pending.TrySetResult(message["result"]);
                }

                return;
            }

            var method = message["method"]?.GetValue<string>();
            var parameters = message["params"] as JsonObject;

            // A request from the agent: it wants something from us and is waiting for the answer.
            if (message["id"] is { } requestId)
            {
                await AnswerAsync(requestId, method, parameters, ct);
                return;
            }

            if (method == "session/update" && parameters?["update"] is JsonObject update)
                await ReportAsync(update, ct);
        }

        /// <summary>Turns one session update into the transcript events the UI already renders.</summary>
        private async Task ReportAsync(JsonObject update, CancellationToken ct)
        {
            var kind = update["sessionUpdate"]?.GetValue<string>();

            switch (kind)
            {
                case "agent_message_chunk":
                    if (TextOf(update["content"]) is { Length: > 0 } text)
                        await events.WriteAsync(AgentEvent.Text_(text), ct);
                    break;

                case "agent_thought_chunk":
                    // Reasoning is not part of the answer; it goes to the log like the CLIs' does.
                    if (TextOf(update["content"]) is { Length: > 0 } thought)
                        await events.WriteAsync(AgentEvent.Log(thought), ct);
                    break;

                case "tool_call":
                    await events.WriteAsync(new AgentEvent(
                        AgentEventKind.Tool,
                        update["title"]?.GetValue<string>() ?? string.Empty)
                    {
                        ToolName = update["kind"]?.GetValue<string>() ?? "tool",
                        ToolCallId = update["toolCallId"]?.GetValue<string>(),
                    }, ct);
                    break;

                case "tool_call_update"
                    when update["status"]?.GetValue<string>() is "completed" or "failed":
                    // in_progress updates say nothing new; only a final one ends the timing.
                    await events.WriteAsync(new AgentEvent(AgentEventKind.ToolCompleted, string.Empty)
                    {
                        ToolCallId = update["toolCallId"]?.GetValue<string>(),
                    }, ct);
                    break;
            }
        }

        private static string? TextOf(JsonNode? content) =>
            content?["type"]?.GetValue<string>() == "text" ? content["text"]?.GetValue<string>() : null;

        // -- answering the agent -----------------------------------------------

        private async Task AnswerAsync(JsonNode id, string? method, JsonObject? parameters, CancellationToken ct)
        {
            JsonNode? result = null;
            string? failure = null;

            try
            {
                result = method switch
                {
                    "session/request_permission" => await DecideAsync(parameters, ct),
                    "fs/read_text_file" => ReadFile(parameters),
                    "fs/write_text_file" => WriteFile(parameters),
                    _ => throw new InvalidOperationException($"'{method}' is not supported by this client."),
                };
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
            };

            if (failure is null)
                response["result"] = result ?? new JsonObject();
            else
                response["error"] = new JsonObject { ["code"] = -32603, ["message"] = failure };

            await SendAsync(response, ct);
        }

        /// <summary>
        /// Answers a permission prompt from the agent's permission mode. Nobody is watching a
        /// scheduled run, so the mode is the whole of the answer: anything short of a mode that
        /// allows edits is a refusal, and the refusal is recorded so it is visible afterwards.
        /// </summary>
        private async Task<JsonNode> DecideAsync(JsonObject? parameters, CancellationToken ct)
        {
            var options = parameters?["options"] as JsonArray ?? [];
            var allow = request.PermissionMode is PermissionMode.AcceptEdits or PermissionMode.Unrestricted;

            var wanted = allow
                ? new[] { "allow_always", "allow_once" }
                : ["reject_once", "reject_always"];

            var chosen = wanted
                .Select(kind => options.FirstOrDefault(o => o?["kind"]?.GetValue<string>() == kind))
                .FirstOrDefault(o => o is not null);

            // An agent is free to offer whatever options it likes; if none of them are the shape we
            // expected, refusing by cancelling is the safe reading.
            if (chosen?["optionId"]?.GetValue<string>() is not { } optionId)
                return new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };

            if (!allow)
            {
                var tool = parameters?["toolCall"]?["title"]?.GetValue<string>() ?? "a tool";
                await events.WriteAsync(new AgentEvent(AgentEventKind.PermissionDenied, tool)
                {
                    ToolName = parameters?["toolCall"]?["kind"]?.GetValue<string>() ?? "tool",
                }, ct);
            }

            return new JsonObject
            {
                ["outcome"] = new JsonObject
                {
                    ["outcome"] = "selected",
                    ["optionId"] = optionId,
                },
            };
        }

        private JsonNode ReadFile(JsonObject? parameters)
        {
            var path = WorkspacePath(parameters?["path"]?.GetValue<string>());
            var lines = File.ReadAllLines(path);

            // Both are optional, and line is 1-based when present.
            var start = Math.Max(0, (parameters?["line"]?.GetValue<int>() ?? 1) - 1);
            var take = parameters?["limit"]?.GetValue<int>() ?? lines.Length;

            var slice = lines.Skip(start).Take(Math.Max(0, take));
            return new JsonObject { ["content"] = string.Join('\n', slice) };
        }

        private JsonNode WriteFile(JsonObject? parameters)
        {
            if (request.PermissionMode is PermissionMode.Plan or PermissionMode.Default)
                throw new InvalidOperationException("This session is read-only.");

            var path = WorkspacePath(parameters?["path"]?.GetValue<string>());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, parameters?["content"]?.GetValue<string>() ?? string.Empty);

            return new JsonObject();
        }

        /// <summary>
        /// Resolves a path the agent asked for and refuses anything outside the session's
        /// workspace. The agent is trusted to do its job, not to be given the whole filesystem.
        /// </summary>
        private string WorkspacePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("No path was given.");

            if (!WorkspacePathGuard.TryResolveWithin(
                    request.WorkingDirectory,
                    path,
                    out var full,
                    _policy.ValidatePaths,
                    _policy.FollowSymlinks))
                throw new InvalidOperationException("That path is outside the workspace.");

            return full;
        }
    }
}
