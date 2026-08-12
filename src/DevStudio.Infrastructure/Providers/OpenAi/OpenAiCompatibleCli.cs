using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Providers.OpenAi;

/// <summary>
/// Talks to an OpenAI-compatible endpoint — llama.cpp's server, ollama, LM Studio — and runs the
/// agent loop itself: advertise the workspace tools, stream the answer, execute whatever the model
/// calls, feed the results back, repeat until it stops asking. The CLIs do this internally; a bare
/// model cannot, which is the whole of the difference between this adapter and the others.
/// </summary>
public sealed class OpenAiCompatibleCli : IProviderCli
{
    private readonly CliProvider _definition;
    private readonly IHttpClientFactory _clients;
    private readonly IProcessRunner _runner;
    private readonly ConversationStore _conversations;
    private readonly ILogger _logger;

    public OpenAiCompatibleCli(
        CliProvider definition,
        IHttpClientFactory clients,
        IProcessRunner runner,
        ConversationStore conversations,
        ILogger logger)
    {
        _definition = definition;
        _clients = clients;
        _runner = runner;
        _conversations = conversations;
        _logger = logger;
    }

    public AiProvider Provider => AiProvider.Custom;
    public string DisplayName => _definition.Name;
    public string? DefinitionId => _definition.Id;

    /// <summary>Nothing to sign into: the endpoint is either reachable or it is not.</summary>
    public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
        (string.Empty, []);

    public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() => (string.Empty, []);

    public async Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_definition.BaseUrl))
            return new ProviderAuthStatus(AiProvider.Custom, ProviderAuthState.LoggedOut, null, "No endpoint is configured.", DateTimeOffset.UtcNow);

        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync("models", ct);

            return response.IsSuccessStatusCode
                ? new ProviderAuthStatus(AiProvider.Custom, ProviderAuthState.LoggedIn, _definition.BaseUrl, "The endpoint answered.", DateTimeOffset.UtcNow)
                : new ProviderAuthStatus(AiProvider.Custom, ProviderAuthState.LoggedOut, null, $"The endpoint answered {(int)response.StatusCode}.", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new ProviderAuthStatus(AiProvider.Custom, ProviderAuthState.LoggedOut, null, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        TurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var events = Channel.CreateUnbounded<AgentEvent>();

        var pump = Task.Run(async () =>
        {
            try
            {
                await ConverseAsync(request, events.Writer, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Turn failed against {BaseUrl}", _definition.BaseUrl);
                await events.Writer.WriteAsync(AgentEvent.Error(ex.Message), CancellationToken.None);
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var evt in events.Reader.ReadAllAsync(CancellationToken.None))
            yield return evt;

        await pump;
    }

    private async Task ConverseAsync(TurnRequest request, ChannelWriter<AgentEvent> events, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_definition.BaseUrl))
            throw new InvalidOperationException($"'{_definition.Name}' has no endpoint configured.");

        var tools = new WorkspaceTools(request.WorkingDirectory, request.PermissionMode, _runner);

        // A forgotten session starts over rather than resuming half a conversation.
        var sessionId = request.ResumeSessionId ?? Guid.NewGuid().ToString("n");
        var messages = _conversations.Get(request.ResumeSessionId);

        var toolDefinitions = tools.Definitions();

        if (messages.Count == 0)
        {
            // A model that is not told it has tools tends to answer from what it knows about
            // itself, which is that it cannot see a filesystem. Saying so plainly, and naming the
            // tools, is what turns a chat model into something that will actually go and look.
            var preamble = Preamble(request.WorkingDirectory, toolDefinitions);

            messages.Add(Message("system", string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? preamble
                : $"{preamble}\n\n{request.SystemPrompt}"));
        }

        messages.Add(Message("user", request.Prompt));
        await events.WriteAsync(new AgentEvent(AgentEventKind.SessionId, sessionId), ct);

        var budget = Math.Max(1, _definition.MaxToolCalls);

        for (var step = 0; step < budget; step++)
        {
            var (text, calls) = await StreamCompletionAsync(messages, toolDefinitions, request, events, ct);

            messages.Add(AssistantMessage(text, calls));

            if (calls.Count == 0)
            {
                _conversations.Set(sessionId, messages);
                await events.WriteAsync(new AgentEvent(AgentEventKind.Result, text), ct);
                return;
            }

            foreach (var call in calls)
            {
                await events.WriteAsync(new AgentEvent(AgentEventKind.Tool, Describe(call))
                {
                    ToolName = call.Name,
                    ToolCallId = call.Id,
                }, ct);

                // This transport runs the tool itself, so the timing is measured rather than inferred.
                var started = Stopwatch.GetTimestamp();
                var result = await tools.InvokeAsync(call.Name, call.Arguments, ct);

                await events.WriteAsync(new AgentEvent(AgentEventKind.ToolCompleted, string.Empty)
                {
                    ToolCallId = call.Id,
                    DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                }, ct);
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = result,
                });
            }
        }

        // Out of steps rather than out of things to say, so the transcript says which.
        _conversations.Set(sessionId, messages);
        await events.WriteAsync(AgentEvent.Error(
            $"Stopped after {budget} tool calls. Raise the limit on the provider if the work needs more."), ct);
    }

    /// <summary>
    /// What the model is told before anything else. Deliberately concrete about the workspace and
    /// about the tools existing: the smaller local models will otherwise refuse work they are
    /// perfectly able to do, on the grounds that models cannot read files.
    /// </summary>
    private static string Preamble(string workspace, JsonArray tools)
    {
        var names = tools
            .Select(t => t?["function"]?["name"]?.GetValue<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n));

        return $"""
                You are a coding agent working in a real checkout on this machine, at {workspace}.

                You have tools, and they run for real: {string.Join(", ", names)}. Use them instead
                of asking the user to paste things in, and instead of saying you cannot see files —
                you can, through these tools. Paths are relative to the workspace root.

                Look before you answer: list or read the files you need, then say what you found.
                """;
    }

    /// <summary>
    /// One request to the endpoint. Text is forwarded as it arrives; tool calls are assembled from
    /// their fragments, since the arguments come through a few characters at a time.
    /// </summary>
    private async Task<(string Text, List<ToolCall> Calls)> StreamCompletionAsync(
        List<JsonObject> messages,
        JsonArray tools,
        TurnRequest request,
        ChannelWriter<AgentEvent> events,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(request.Model) ? _definition.Models.FirstOrDefault() ?? "local" : request.Model,
            ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)m.DeepClone())]),
            ["tools"] = tools.DeepClone(),
            ["stream"] = _definition.Stream,
        };

        using var client = CreateClient();
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = content };

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"The endpoint answered {(int)response.StatusCode}: {Trim(detail)}");
        }

        var text = new StringBuilder();
        var calls = new SortedDictionary<int, ToolCall>();

        if (!_definition.Stream)
        {
            var whole = await response.Content.ReadAsStringAsync(ct);
            ReadChoice(JsonNode.Parse(whole)?["choices"]?[0] as JsonObject, text, calls);

            if (text.Length > 0)
                await events.WriteAsync(AgentEvent.Text_(text.ToString()), ct);

            return (text.ToString(), [.. calls.Values]);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            // Server-sent events: the payload lines are the ones that matter, and the stream ends
            // with a sentinel rather than by closing.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line[5..].Trim();
            if (payload is "[DONE]")
                break;

            JsonObject? chunk;
            try
            {
                chunk = JsonNode.Parse(payload) as JsonObject;
            }
            catch (JsonException)
            {
                _logger.LogDebug("Ignoring unparsable chunk: {Payload}", Trim(payload));
                continue;
            }

            if (chunk?["choices"]?[0] is not JsonObject choice)
                continue;

            var before = text.Length;
            ReadChoice(choice, text, calls);

            if (text.Length > before)
                await events.WriteAsync(AgentEvent.Text_(text.ToString()[before..]), ct);
        }

        return (text.ToString(), [.. calls.Values]);
    }

    /// <summary>
    /// Reads one choice, whether it is a streamed delta or a whole message — the two carry the same
    /// fields under different names, and some servers send a whole message even when streaming.
    /// </summary>
    private static void ReadChoice(JsonObject? choice, StringBuilder text, SortedDictionary<int, ToolCall> calls)
    {
        if ((choice?["delta"] as JsonObject ?? choice?["message"] as JsonObject) is not { } part)
            return;

        if (part["content"]?.GetValue<string>() is { Length: > 0 } piece)
            text.Append(piece);

        if (part["tool_calls"] is JsonArray fragments)
            Accumulate(calls, fragments);
    }

    /// <summary>
    /// Tool calls arrive split across chunks — an id and a name in one, the arguments a few
    /// characters at a time after it — and are identified by their index within the message.
    /// </summary>
    private static void Accumulate(SortedDictionary<int, ToolCall> calls, JsonArray fragments)
    {
        foreach (var fragment in fragments.OfType<JsonObject>())
        {
            var index = fragment["index"]?.GetValue<int>() ?? 0;
            if (!calls.TryGetValue(index, out var call))
                calls[index] = call = new ToolCall();

            if (fragment["id"]?.GetValue<string>() is { Length: > 0 } id)
                call.Id = id;

            if (fragment["function"] is not JsonObject function)
                continue;

            if (function["name"]?.GetValue<string>() is { Length: > 0 } name)
                call.Name = name;

            if (function["arguments"]?.GetValue<string>() is { Length: > 0 } arguments)
                call.ArgumentFragments.Append(arguments);
        }
    }

    private static JsonObject AssistantMessage(string text, List<ToolCall> calls)
    {
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text,
        };

        if (calls.Count == 0)
            return message;

        message["tool_calls"] = new JsonArray([.. calls.Select(call => (JsonNode)new JsonObject
        {
            ["id"] = call.Id,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = call.Name,
                ["arguments"] = call.Arguments,
            },
        })]);

        return message;
    }

    private static JsonObject Message(string role, string content) =>
        new() { ["role"] = role, ["content"] = content };

    private static string Describe(ToolCall call) => Trim(call.Arguments, 200);

    private static string Trim(string text, int limit = 500) =>
        text.Length <= limit ? text : text[..limit] + "…";

    private HttpClient CreateClient()
    {
        var client = _clients.CreateClient(nameof(OpenAiCompatibleCli));

        // Relative request paths are resolved against this, so the trailing slash matters.
        client.BaseAddress = new Uri(_definition.BaseUrl.TrimEnd('/') + "/");

        // A local model can think for a long time before its first token.
        client.Timeout = TimeSpan.FromMinutes(30);

        if (!string.IsNullOrWhiteSpace(_definition.ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _definition.ApiKey);

        return client;
    }

    private sealed class ToolCall
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder ArgumentFragments { get; } = new();
        public string Arguments => ArgumentFragments.ToString();
    }
}
