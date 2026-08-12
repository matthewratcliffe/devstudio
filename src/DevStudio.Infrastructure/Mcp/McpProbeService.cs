using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Mcp;

/// <summary>
/// A deliberately small MCP client: enough to open a connection, initialize, and then either ask what
/// tools a server offers or run one of them. It exists so a misconfigured server is caught here rather
/// than halfway through an agent run, where the failure is far harder to read.
/// </summary>
public sealed class McpProbeService : IMcpProbeService
{
    private const string ProtocolVersion = "2025-06-18";

    /// <summary>The id used for whatever request follows initialize; initialize itself is always 1.</summary>
    private const int FollowUpId = 2;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _clients;
    private readonly IMcpTokenService _tokens;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<McpProbeService> _logger;

    public McpProbeService(
        IHttpClientFactory clients,
        IMcpTokenService tokens,
        IOptions<OrchestratorOptions> options,
        ILogger<McpProbeService> logger)
    {
        _clients = clients;
        _tokens = tokens;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>One initialize plus one follow-up request, however the transport carries them.</summary>
    private sealed record Exchange(JsonObject? Initialize, JsonObject? Response, string? Failure);

    public async Task<McpProbeResult> ListToolsAsync(McpServer server, CancellationToken ct = default)
    {
        var exchange = await ExchangeAsync(server, ToolsListRequest(), "tools/list", ct);

        return exchange.Failure is { } failure
            ? Failed(failure)
            : Interpret(exchange.Initialize!, exchange.Response!);
    }

    public async Task<McpToolCallResult> CallToolAsync(
        McpServer server,
        string toolName,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return new McpToolCallResult(false, "No tool was named.");

        JsonObject arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? new JsonObject()
                : JsonNode.Parse(argumentsJson) as JsonObject
                  ?? throw new JsonException("the value is not an object");
        }
        catch (JsonException ex)
        {
            return new McpToolCallResult(false, $"Arguments are not a JSON object: {ex.Message}");
        }

        var exchange = await ExchangeAsync(server, ToolsCallRequest(toolName, arguments), "tools/call", ct);

        return exchange.Failure is { } failure
            ? new McpToolCallResult(false, failure)
            : InterpretCall(exchange.Response!);
    }

    /// <summary>Opens a connection, initializes it, and sends one further request over the same session.</summary>
    private async Task<Exchange> ExchangeAsync(McpServer server, JsonObject followUp, string method, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            return server.Transport == McpTransport.Stdio
                ? await ExchangeStdioAsync(server, followUp, method, timeout.Token)
                : await ExchangeHttpAsync(server, followUp, method, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Exchange(null, null, "The server did not answer within 30 seconds.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Talking to MCP server {Server} failed", server.Name);
            return new Exchange(null, null, ex.Message);
        }
    }

    // -- stdio -----------------------------------------------------------------

    private async Task<Exchange> ExchangeStdioAsync(McpServer server, JsonObject followUp, string method, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
            return new Exchange(null, null, "No command is configured.");

        var info = new ProcessStartInfo
        {
            FileName = server.Command,
            WorkingDirectory = _options.ScratchPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var argument in server.Arguments)
            info.ArgumentList.Add(argument);

        info.Environment["HOME"] = _options.HomePath;
        foreach (var pair in server.Environment)
            info.Environment[pair.Key] = pair.Value;

        Directory.CreateDirectory(_options.ScratchPath);

        using var process = new Process { StartInfo = info };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new Exchange(null, null, $"Could not start '{server.Command}': {ex.Message}");
        }

        try
        {
            await WriteAsync(process.StandardInput, InitializeRequest(), ct);
            var initialize = await ReadResponseAsync(process.StandardOutput, 1, ct);
            if (initialize is null)
                return new Exchange(null, null, "The server exited before answering initialize. Check the command and its arguments.");

            if (initialize["error"] is not null)
                return new Exchange(initialize, null, $"initialize failed: {Describe(initialize["error"])}");

            await WriteAsync(process.StandardInput, InitializedNotification(), ct);
            await WriteAsync(process.StandardInput, followUp, ct);

            var response = await ReadResponseAsync(process.StandardOutput, FollowUpId, ct);

            return response is null
                ? new Exchange(initialize, null, $"The server did not answer {method}.")
                : new Exchange(initialize, response, null);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
        }
    }

    private static async Task WriteAsync(StreamWriter stdin, JsonObject message, CancellationToken ct)
    {
        await stdin.WriteAsync(message.ToJsonString().AsMemory(), ct);
        await stdin.WriteAsync("\n".AsMemory(), ct);
        await stdin.FlushAsync(ct);
    }

    /// <summary>Reads lines until one carries the id we asked about. Anything else is a notification.</summary>
    private static async Task<JsonObject?> ReadResponseAsync(StreamReader stdout, int id, CancellationToken ct)
    {
        while (await stdout.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is JsonObject message && message["id"]?.GetValue<int>() == id)
                return message;
        }

        return null;
    }

    // -- http / sse ------------------------------------------------------------

    private async Task<Exchange> ExchangeHttpAsync(McpServer server, JsonObject followUp, string method, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
            return new Exchange(null, null, "No URL is configured.");

        var client = _clients.CreateClient(nameof(McpProbeService));
        client.Timeout = Timeout;

        var headers = new Dictionary<string, string>(server.Headers);
        if (await _tokens.GetAccessTokenAsync(server, ct) is { Length: > 0 } token)
        {
            var prefix = string.IsNullOrWhiteSpace(server.AuthHeaderPrefix) ? string.Empty : server.AuthHeaderPrefix + " ";
            headers[string.IsNullOrWhiteSpace(server.AuthHeaderName) ? "Authorization" : server.AuthHeaderName] = prefix + token;
        }

        var (initialize, sessionId) = await PostAsync(client, server.Url, InitializeRequest(), headers, null, ct);
        if (initialize is null)
            return new Exchange(null, null, "The server did not return a usable initialize response.");

        if (initialize["error"] is not null)
            return new Exchange(initialize, null, $"initialize failed: {Describe(initialize["error"])}");

        // The spec wants this notification before anything else is requested.
        await PostAsync(client, server.Url, InitializedNotification(), headers, sessionId, ct);

        var (response, _) = await PostAsync(client, server.Url, followUp, headers, sessionId, ct);

        return response is null
            ? new Exchange(initialize, null, $"The server did not answer {method}.")
            : new Exchange(initialize, response, null);
    }

    private async Task<(JsonObject? Message, string? SessionId)> PostAsync(
        HttpClient client,
        string url,
        JsonObject payload,
        IReadOnlyDictionary<string, string> headers,
        string? sessionId,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // Streamable HTTP servers may answer with either JSON or an SSE stream.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        foreach (var pair in headers)
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);

        if (sessionId is { Length: > 0 })
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var returnedSession = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault()
            : sessionId;

        if (!response.IsSuccessStatusCode)
        {
            return (new JsonObject
            {
                ["error"] = new JsonObject { ["message"] = $"HTTP {(int)response.StatusCode}. {Summarise(body)}" },
            }, returnedSession);
        }

        return (ParseBody(body), returnedSession);
    }

    /// <summary>Accepts a plain JSON body or an SSE stream, returning the first JSON-RPC message.</summary>
    private static JsonObject? ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                return JsonNode.Parse(trimmed) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0)
                continue;

            try
            {
                if (JsonNode.Parse(payload) is JsonObject message && message["id"] is not null)
                    return message;
            }
            catch (JsonException)
            {
                // Keep looking; a stream can carry comments and keep-alives.
            }
        }

        return null;
    }

    // -- shared ----------------------------------------------------------------

    private static JsonObject InitializeRequest() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "devstudio", ["version"] = "1.0.0" },
        },
    };

    private static JsonObject InitializedNotification() => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "notifications/initialized",
    };

    private static JsonObject ToolsListRequest() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = FollowUpId,
        ["method"] = "tools/list",
        ["params"] = new JsonObject(),
    };

    private static JsonObject ToolsCallRequest(string toolName, JsonObject arguments) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = FollowUpId,
        ["method"] = "tools/call",
        ["params"] = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments,
        },
    };

    private static McpProbeResult Interpret(JsonObject initialize, JsonObject toolsResponse)
    {
        if (toolsResponse["error"] is { } error)
            return Failed($"tools/list failed: {Describe(error)}");

        var serverInfo = initialize["result"]?["serverInfo"];
        var name = serverInfo?["name"]?.GetValue<string>();
        var version = serverInfo?["version"]?.GetValue<string>();

        var tools = new List<McpToolInfo>();
        if (toolsResponse["result"]?["tools"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                var toolName = entry["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(toolName))
                    continue;

                tools.Add(new McpToolInfo(
                    toolName,
                    entry["description"]?.GetValue<string>() ?? string.Empty,
                    entry["inputSchema"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            }
        }

        var detail = tools.Count == 0
            ? "Connected, but the server offers no tools."
            : $"Connected. {tools.Count} tool{(tools.Count == 1 ? "" : "s")} available.";

        return new McpProbeResult(true, detail, tools, name, version);
    }

    private static McpToolCallResult InterpretCall(JsonObject response)
    {
        if (response["error"] is { } error)
            return new McpToolCallResult(false, $"tools/call failed: {Describe(error)}");

        var result = response["result"];

        // A tool that fails answers normally and sets isError, so success is not something the
        // transport can decide for us.
        var isError = result?["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;

        return new McpToolCallResult(
            !isError,
            isError ? "The tool ran and reported an error." : "The tool ran.",
            Flatten(result));
    }

    /// <summary>Turns the content blocks into plain text, falling back to the raw result.</summary>
    private static string? Flatten(JsonNode? result)
    {
        if (result is null)
            return null;

        if (result["content"] is JsonArray blocks)
        {
            var text = new StringBuilder();
            foreach (var block in blocks.OfType<JsonObject>())
            {
                if (text.Length > 0)
                    text.AppendLine();

                // Images and resource links carry no text; name them so the pane is not blank.
                text.Append(block["text"]?.GetValue<string>() is { Length: > 0 } value
                    ? value
                    : $"[{block["type"]?.GetValue<string>() ?? "content"}]");
            }

            if (text.Length > 0)
                return text.ToString();
        }

        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static McpProbeResult Failed(string detail) => new(false, detail, []);

    private static string Describe(JsonNode? error) =>
        error?["message"]?.GetValue<string>() ?? error?.ToJsonString() ?? "unknown error";

    private static string Summarise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var text = body.Length <= 200 ? body : body[..200];
        return text.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
