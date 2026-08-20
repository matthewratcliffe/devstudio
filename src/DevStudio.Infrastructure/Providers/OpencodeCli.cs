using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Sessions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Providers;

/// <summary>
/// Talks to a running <c>opencode serve</c> instance over its HTTP API rather than shelling out —
/// opencode's own server holds sessions, model config and whatever credentials it was started with,
/// so this app only ever needs the base URL it is listening on (e.g. <c>http://localhost:8083</c>).
/// There is nothing for this app to sign into: the server is either reachable or it is not.
/// </summary>
public sealed class OpencodeCli : IProviderCli
{
    private readonly IHttpClientFactory _clients;
    private readonly IOpencodeServerManager _server;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<OpencodeCli> _logger;

    public OpencodeCli(
        IHttpClientFactory clients,
        IOpencodeServerManager server,
        IOptions<OrchestratorOptions> options,
        ILogger<OpencodeCli> logger)
    {
        _clients = clients;
        _server = server;
        _options = options.Value;
        _logger = logger;
    }

    public AiProvider Provider => AiProvider.Opencode;
    public string DisplayName => "OpenCode";
    public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) => (string.Empty, []);
    public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() => (string.Empty, []);

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        TurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var pump = Task.Run(async () =>
        {
            try
            {
                await ConverseAsync(request, channel.Writer, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "opencode turn failed against {BaseUrl}", _options.OpencodeBaseUrl);
                await channel.Writer.WriteAsync(AgentEvent.Error(ex.Message), CancellationToken.None);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var evt in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return evt;

        await pump;
    }

    private async Task ConverseAsync(TurnRequest request, ChannelWriter<AgentEvent> events, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.OpencodeBaseUrl))
            throw new InvalidOperationException("No opencode server URL is configured.");

        await _server.EnsureRunningAsync(ct);

        using var client = CreateClient(request.WorkingDirectory);

        // A resumed conversation is an existing opencode session id; a fresh one has to be created
        // first, since opencode has no notion of a stateless turn.
        var sessionId = request.ResumeSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            using var createBody = new StringContent("{}", Encoding.UTF8, "application/json");
            using var createResponse = await client.PostAsync("session", createBody, ct);
            createResponse.EnsureSuccessStatusCode();
            var created = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync(ct));
            sessionId = created?["id"]?.GetValue<string>()
                ?? throw new InvalidOperationException("opencode did not return a session id.");
        }

        await events.WriteAsync(new AgentEvent(AgentEventKind.SessionId, sessionId), ct);

        // Chosen up front and handed to the server as this turn's user message id, rather than
        // letting opencode mint one, purely so the event listener can tell the prompt we are about
        // to send apart from what the model says back — the server echoes every message's own parts
        // over /event too, and without this the system prompt we send shows up in the transcript as
        // if the assistant had said it.
        var userMessageId = "msg_" + Guid.NewGuid().ToString("N");

        // The server's /event stream is what turns this into a streaming provider: it publishes
        // text and tool-call deltas live as the model runs, while the /message call below only
        // resolves once the whole turn is finished. Both hit the same session, so listening on
        // one and posting to the other gets deltas as they happen and a definitive finish signal.
        using var eventsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var listenTask = Task.Run(() => ListenAsync(client, sessionId, userMessageId, request.PermissionMode, events, eventsCts.Token), CancellationToken.None);

        try
        {
            var slashCommand = ParseSlashCommand(request.Prompt);
            var commandName = slashCommand is null
                ? null
                : await ResolveCommandNameAsync(client, slashCommand.Value.Name, ct);

            JsonObject body;
            string endpoint;

            if (slashCommand is { } parsed && commandName is not null)
            {
                // opencode's slash commands are a server-side concept — a template it expands and
                // runs — not something the model interprets from plain text the way Claude Code's
                // and Codex's own CLI processes do, so it needs its own endpoint rather than just
                // being typed into the message body.
                endpoint = $"session/{sessionId}/command";
                body = new JsonObject
                {
                    ["messageID"] = userMessageId,
                    ["command"] = commandName,
                    ["arguments"] = parsed.Arguments,
                };
            }
            else
            {
                var prompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
                    ? request.Prompt
                    : $"{request.SystemPrompt}\n\n---\n\n{request.Prompt}";

                endpoint = $"session/{sessionId}/message";
                body = new JsonObject
                {
                    ["messageID"] = userMessageId,
                    ["parts"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }),
                };
            }

            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                // opencode names a model as "provider/model"; anything without a slash is assumed
                // to already belong to the provider its server was configured with.
                var slash = request.Model.IndexOf('/');
                var model = new JsonObject();
                if (slash > 0)
                {
                    model["providerID"] = request.Model[..slash];
                    model["modelID"] = request.Model[(slash + 1)..];
                }
                else
                {
                    model["modelID"] = request.Model;
                }
                body["model"] = model;
            }

            using var messageBody = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var messageResponse = await client.PostAsync(endpoint, messageBody, ct);

            if (!messageResponse.IsSuccessStatusCode)
            {
                var detail = await messageResponse.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"opencode answered {(int)messageResponse.StatusCode}: {Trim(detail)}");
            }

            var result = JsonNode.Parse(await messageResponse.Content.ReadAsStringAsync(ct));

            if (result?["info"]?["error"] is JsonObject error)
            {
                await events.WriteAsync(AgentEvent.Error(DescribeError(error)), ct);
            }
            else
            {
                var text = new StringBuilder();
                if (result?["parts"] is JsonArray parts)
                {
                    foreach (var part in parts)
                    {
                        if (part?["type"]?.GetValue<string>() == "text" && part["text"]?.GetValue<string>() is { Length: > 0 } piece)
                            text.Append(piece);
                    }
                }

                await events.WriteAsync(new AgentEvent(AgentEventKind.Result, text.ToString()), ct);
            }
        }
        finally
        {
            // The turn is over; nothing more will arrive for this session, so the listener's job
            // is done. It has already forwarded everything it saw while the turn was running.
            eventsCts.Cancel();
            try
            {
                await listenTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Reads the server's <c>/event</c> SSE stream and forwards deltas for the given session until
    /// cancelled. Runs alongside the blocking <c>/message</c> call so the transcript fills in live
    /// rather than arriving as one lump when the turn finishes.
    /// </summary>
    private async Task ListenAsync(HttpClient client, string sessionId, string userMessageId, PermissionMode permissionMode, ChannelWriter<AgentEvent> events, CancellationToken ct)
    {
        using var response = await client.GetAsync("event", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var toolsSeen = new HashSet<string>();
        var toolsCompleted = new HashSet<string>();
        var textSent = new Dictionary<string, int>();
        var usageCounted = new HashSet<string>();
        var data = (string?)null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data = line["data:".Length..].TrimStart();
                continue;
            }

            // A blank line ends one SSE frame; anything else (event:, id:, comments) is ignored
            // since the data payload already carries the id and type.
            if (line.Length > 0 || data is null)
                continue;

            var frame = data;
            data = null;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(frame);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (node?["type"]?.GetValue<string>() == "permission.asked")
            {
                var evt = await RespondToPermissionAsync(client, node, sessionId, permissionMode, ct);
                if (evt is not null)
                    await events.WriteAsync(evt, CancellationToken.None);
                continue;
            }

            foreach (var evt in TranslateEvent(node, sessionId, userMessageId, toolsSeen, toolsCompleted, textSent, usageCounted))
                await events.WriteAsync(evt, CancellationToken.None);
        }
    }

    /// <summary>
    /// Answers a permission prompt from the turn's permission mode. Nobody is watching a scheduled
    /// run, so the mode is the whole of the answer: anything short of a mode that allows edits is a
    /// refusal, mirroring <c>AcpCli</c>'s handling of the same decision for ACP-driven providers.
    /// </summary>
    private async Task<AgentEvent?> RespondToPermissionAsync(HttpClient client, JsonNode node, string sessionId, PermissionMode permissionMode, CancellationToken ct)
    {
        var properties = node["properties"];
        if (properties?["sessionID"]?.GetValue<string>() != sessionId)
            return null;

        var permissionId = properties["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(permissionId))
            return null;

        var allow = permissionMode is PermissionMode.AcceptEdits or PermissionMode.Unrestricted;
        var reply = allow ? "once" : "reject";

        try
        {
            using var body = new StringContent(
                new JsonObject { ["reply"] = reply }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"permission/{permissionId}/reply", body, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not reply to opencode permission request {PermissionId}", permissionId);
            return null;
        }

        if (allow)
            return null;

        var tool = properties["permission"]?.GetValue<string>() ?? "a tool";
        return new AgentEvent(AgentEventKind.PermissionDenied, tool) { ToolName = tool };
    }

    /// <summary>Maps one <c>/event</c> frame onto transcript events, ignoring anything outside the turn's session.</summary>
    private static IEnumerable<AgentEvent> TranslateEvent(
        JsonNode? node,
        string sessionId,
        string userMessageId,
        HashSet<string> toolsSeen,
        HashSet<string> toolsCompleted,
        Dictionary<string, int> textSent,
        HashSet<string> usageCounted)
    {
        var type = node?["type"]?.GetValue<string>();
        var properties = node?["properties"];
        if (type is null || properties is null)
            yield break;

        if (properties["sessionID"]?.GetValue<string>() is { Length: > 0 } propertySession && propertySession != sessionId)
            yield break;

        // Every part and delta names the message it belongs to. The server echoes the prompt we
        // just sent back over this same stream like any other message, and without this check that
        // echo — system prompt included — would land in the transcript as if the assistant had
        // said it, since nothing else here distinguishes "what we sent" from "what came back".
        var messageId = properties["messageID"]?.GetValue<string>() ?? properties["part"]?["messageID"]?.GetValue<string>();
        if (messageId == userMessageId)
            yield break;

        switch (type)
        {
            // The live delta feed. field is always "text" today, but anything else is skipped
            // rather than guessed at.
            case "message.part.delta":
                if (properties["field"]?.GetValue<string>() == "text" &&
                    properties["delta"]?.GetValue<string>() is { Length: > 0 } delta &&
                    properties["partID"]?.GetValue<string>() is { Length: > 0 } deltaPartId)
                {
                    textSent[deltaPartId] = textSent.GetValueOrDefault(deltaPartId) + delta.Length;
                    yield return AgentEvent.Text_(delta);
                }
                break;

            case "message.part.updated":
                if (properties["part"] is not JsonObject part)
                    break;

                var partType = part["type"]?.GetValue<string>();
                var partId = part["id"]?.GetValue<string>();

                if (partType == "text" && partId is not null &&
                    part["text"]?.GetValue<string>() is { } fullText)
                {
                    // Only providers that skip the delta feed land here; anything the deltas
                    // above already streamed is not repeated.
                    var sent = textSent.GetValueOrDefault(partId);
                    if (fullText.Length > sent)
                    {
                        yield return AgentEvent.Text_(fullText[sent..]);
                        textSent[partId] = fullText.Length;
                    }
                }
                else if (partType == "tool" &&
                    part["callID"]?.GetValue<string>() is { Length: > 0 } callId)
                {
                    var status = part["state"]?["status"]?.GetValue<string>();
                    var toolName = part["tool"]?.GetValue<string>() ?? "tool";

                    if (toolsSeen.Add(callId))
                    {
                        yield return new AgentEvent(AgentEventKind.Tool, DescribeToolInput(part["state"]?["input"]))
                        {
                            ToolName = toolName,
                            ToolCallId = callId,
                        };
                    }

                    if (status is "completed" or "error" && toolsCompleted.Add(callId))
                        yield return new AgentEvent(AgentEventKind.ToolCompleted, string.Empty) { ToolCallId = callId };
                }
                else if (partType == "step-finish" && partId is not null && usageCounted.Add(partId) &&
                    part["tokens"] is JsonObject tokens)
                {
                    yield return new AgentEvent(AgentEventKind.Usage, partId)
                    {
                        Usage = new TokenUsage(
                            (int)(tokens["input"]?.GetValue<double>() ?? 0),
                            (int)(tokens["output"]?.GetValue<double>() ?? 0) + (int)(tokens["reasoning"]?.GetValue<double>() ?? 0),
                            (int)(tokens["cache"]?["read"]?.GetValue<double>() ?? 0),
                            (int)(tokens["cache"]?["write"]?.GetValue<double>() ?? 0)),
                    };
                }
                break;

            case "session.error":
                if (properties["error"] is JsonObject sessionError)
                    yield return AgentEvent.Error(DescribeError(sessionError));
                break;
        }
    }

    /// <summary>Shows the field that identifies what the tool acted on, not the whole payload.</summary>
    private static string DescribeToolInput(JsonNode? input)
    {
        if (input is not JsonObject inputObject)
            return string.Empty;

        foreach (var key in new[] { "command", "filePath", "file_path", "path", "pattern", "url", "description", "prompt" })
        {
            if (inputObject[key]?.GetValue<string>() is { Length: > 0 } value)
                return value.Length <= 200 ? value : value[..197] + "...";
        }

        return string.Empty;
    }

    /// <summary>A leading "/name rest-of-text" in a prompt, the same shape Claude Code and Codex treat as a slash command.</summary>
    private readonly record struct SlashCommand(string Name, string Arguments);

    private static SlashCommand? ParseSlashCommand(string prompt)
    {
        var text = prompt.TrimStart();
        if (!text.StartsWith('/'))
            return null;

        var newline = text.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? text : text[..newline];

        var space = line.IndexOfAny([' ', '\t']);
        var name = (space < 0 ? line : line[..space])[1..];
        if (name.Length == 0)
            return null;

        var arguments = space < 0 ? string.Empty : line[(space + 1)..].TrimStart();
        return new SlashCommand(name, arguments);
    }

    /// <summary>
    /// Confirms a parsed "/name" actually names one of opencode's registered commands before this
    /// turn is routed to the command endpoint — otherwise ordinary text that merely starts with a
    /// slash (a path, a fraction) would be misread as a command and fail.
    /// </summary>
    private async Task<string?> ResolveCommandNameAsync(HttpClient client, string name, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync("command", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var commands = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonArray ?? [];
            foreach (var command in commands)
            {
                if (string.Equals(command?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                    return command!["name"]!.GetValue<string>();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check opencode's registered commands");
            return null;
        }
    }

    private static string DescribeError(JsonObject error)
    {
        var message = error["data"]?["message"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(message))
            return message;

        var name = error["name"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(name) ? "opencode reported an error." : name;
    }

    public async Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.OpencodeBaseUrl))
            return new ProviderAuthStatus(Provider, ProviderAuthState.LoggedOut, null, "No opencode server URL is configured.", DateTimeOffset.UtcNow);

        try
        {
            await _server.EnsureRunningAsync(ct);

            using var client = CreateClient();
            using var response = await client.GetAsync("app", ct);

            return response.IsSuccessStatusCode
                ? new ProviderAuthStatus(Provider, ProviderAuthState.LoggedIn, _options.OpencodeBaseUrl, "The server answered.", DateTimeOffset.UtcNow)
                : new ProviderAuthStatus(Provider, ProviderAuthState.LoggedOut, null, $"The server answered {(int)response.StatusCode}.", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new ProviderAuthStatus(Provider, ProviderAuthState.LoggedOut, null, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Models the server actually knows about right now — whatever providers it has credentials or
    /// config for, each with the models that provider offers. Used to fill the model picker with
    /// live choices instead of the fixed suggestions <see cref="OrchestratorOptions.OpencodeModels"/>
    /// carries, which go stale as opencode adds and renames models.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.OpencodeBaseUrl))
            return [];

        try
        {
            await _server.EnsureRunningAsync(ct);

            using var client = CreateClient();
            using var response = await client.GetAsync("config/providers", ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (result?["providers"] is not JsonArray providers)
                return [];

            var models = new List<string>();
            foreach (var provider in providers)
            {
                var providerId = provider?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(providerId) || provider?["models"] is not JsonObject modelMap)
                    continue;

                foreach (var (modelId, _) in modelMap)
                    models.Add($"{providerId}/{modelId}");
            }

            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not list opencode's available models");
            return [];
        }
    }

    /// <summary>
    /// Commands the server currently has registered for the project — its own built-ins plus
    /// whatever a project or user has defined — so the UI can offer live autocomplete instead of a
    /// list that would go stale the moment someone adds a command file.
    /// </summary>
    public async Task<IReadOnlyList<CliSlashCommand>> GetAvailableSlashCommandsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.OpencodeBaseUrl))
            return [];

        try
        {
            await _server.EnsureRunningAsync(ct);

            using var client = CreateClient();
            using var response = await client.GetAsync("command", ct);
            if (!response.IsSuccessStatusCode)
                return [];

            var commands = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonArray ?? [];
            return [.. commands
                .Select(command => command?["name"]?.GetValue<string>() is { Length: > 0 } name
                    ? new CliSlashCommand(
                        "/" + name,
                        command["description"]?.GetValue<string>() ?? string.Empty,
                        "/" + name + (command["hints"] is JsonArray { Count: > 0 } ? " <args>" : ""))
                    : null)
                .Where(command => command is not null)!];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not list opencode's registered commands");
            return [];
        }
    }

    private HttpClient CreateClient(string? workingDirectory = null)
    {
        var client = _clients.CreateClient(nameof(OpencodeCli));
        client.BaseAddress = new Uri(_options.OpencodeBaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromMinutes(30);

        // Opencode's server is not scoped to one project: every request names which project
        // (directory) it is about, defaulting to the server process's own cwd when it is not told.
        // Without this every turn would run against whatever directory the server happened to
        // start in, rather than the workspace the turn was actually given.
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            client.DefaultRequestHeaders.TryAddWithoutValidation("x-opencode-directory", workingDirectory);

        return client;
    }

    private static string Trim(string text, int limit = 500) =>
        text.Length <= limit ? text : text[..limit] + "…";
}
