using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Providers;

/// <summary>
/// Drives the <c>codex</c> CLI through <c>codex exec --json</c>. Codex has no
/// append-system-prompt flag, so the agent's instructions are folded into the prompt instead.
/// </summary>
public sealed class CodexCli : IProviderCli
{
    private readonly IProcessRunner _runner;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<CodexCli> _logger;

    public CodexCli(IProcessRunner runner, IOptions<OrchestratorOptions> options, ILogger<CodexCli> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public AiProvider Provider => AiProvider.Codex;
    public string DisplayName => "OpenAI Codex";

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        TurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffered = new List<AgentEvent>();
        var meaningful = false;
        var failed = false;

        await foreach (var evt in RunTurnOnceAsync(request, ct))
        {
            buffered.Add(evt);
            meaningful |= evt.Kind is AgentEventKind.Text or AgentEventKind.Tool or AgentEventKind.Result;
            failed |= evt.Kind == AgentEventKind.Error;
        }

        if (failed && !meaningful && !string.IsNullOrWhiteSpace(request.FallbackHomeDirectory))
        {
            yield return AgentEvent.Log("Primary Codex account failed before producing output; trying the backup account.");

            await foreach (var evt in RunTurnOnceAsync(request with
            {
                ResumeSessionId = null,
                HomeDirectory = request.FallbackHomeDirectory,
                FallbackHomeDirectory = null,
            }, ct))
                yield return evt;

            yield break;
        }

        foreach (var evt in buffered)
            yield return evt;
    }

    private async IAsyncEnumerable<AgentEvent> RunTurnOnceAsync(
        TurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var arguments = BuildArguments(request);

        // Partial and finished text arrive on separate events, so the turn keeps track of how
        // much of the current message has already been sent to the transcript.
        var stream = new MessageStream();

        var pump = Task.Run(async () =>
        {
            try
            {
                var exitCode = await _runner.StreamAsync(
                    new ProcessRequest(
                        _options.CodexExecutable,
                        arguments,
                        request.WorkingDirectory,
                        BuildEnvironment(request),
                        StandardInput: BuildPrompt(request),
                        TimeoutSeconds: 0),
                    async (line, isError, _) =>
                    {
                        foreach (var evt in Translate(line, isError, stream))
                            await channel.Writer.WriteAsync(evt, CancellationToken.None);
                    },
                    ct);

                if (exitCode != 0)
                {
                    await channel.Writer.WriteAsync(
                        AgentEvent.Error($"codex exited with code {exitCode}."), CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "codex turn failed");
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

    private List<string> BuildArguments(TurnRequest request)
    {
        var arguments = new List<string> { "exec" };

        // `exec resume` takes a much smaller set of flags than `exec` does, and codex exits with a
        // usage error rather than ignoring one it does not know. That is why only the first message
        // of a session ever worked: every reply after it resumes.
        var resuming = !string.IsNullOrWhiteSpace(request.ResumeSessionId);
        if (resuming)
        {
            arguments.Add("resume");
            arguments.Add(request.ResumeSessionId!);
        }

        arguments.Add("--json");
        arguments.Add("--skip-git-repo-check");

        if (!resuming)
        {
            // Not accepted when resuming; the process already runs in the workspace either way.
            arguments.Add("--cd");
            arguments.Add(request.WorkingDirectory);
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model!);
        }

        if (!string.IsNullOrWhiteSpace(request.Effort))
        {
            // Codex exposes reasoning depth as config rather than a flag.
            arguments.Add("-c");
            arguments.Add($"model_reasoning_effort=\"{request.Effort}\"");
        }

        switch (request.PermissionMode)
        {
            case PermissionMode.Plan:
            case PermissionMode.Default:
                AddSandbox("read-only");
                break;
            case PermissionMode.AcceptEdits:
                if (_options.ContainerIsTheSandbox)
                {
                    // codex sandboxes through its own bundled bubblewrap, which cannot create a
                    // namespace where the host forbids it — bwrap fails and the command never runs.
                    // The container is the boundary instead, so codex is told not to build one.
                    AddSandbox("danger-full-access");
                }
                else
                {
                    AddSandbox("workspace-write");

                    // That sandbox blocks network by default, and a forge CLI that cannot reach its
                    // host fails in a way that reads as a broken login rather than a closed socket.
                    arguments.Add("-c");
                    arguments.Add("sandbox_workspace_write.network_access=true");
                }

                break;
            case PermissionMode.Unrestricted:
                // This one resume does accept, and it implies the approval policy below.
                arguments.Add("--dangerously-bypass-approvals-and-sandbox");
                break;
        }

        if (request.PermissionMode != PermissionMode.Unrestricted)
        {
            // Nobody is watching a headless run, so a request for approval is answered by nothing
            // and comes back to the model as a cancelled call — which reads as the tool being
            // broken or the connector being unauthorised. The sandbox is the guard here, not a
            // prompt: inside it the model may act, and outside it the attempt simply fails.
            arguments.Add("-c");
            arguments.Add("approval_policy=\"never\"");
        }

        arguments.AddRange(McpOverrides(request.WorkingDirectory));

        // --sandbox is an `exec` flag only, so resuming sets the same thing through config, which
        // both subcommands take.
        void AddSandbox(string mode)
        {
            if (resuming)
            {
                arguments.Add("-c");
                arguments.Add($"sandbox_mode=\"{mode}\"");
            }
            else
            {
                arguments.Add("--sandbox");
                arguments.Add(mode);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ExtraArguments))
            arguments.AddRange(ClaudeCli.SplitArguments(request.ExtraArguments!));

        // The prompt travels on stdin rather than as a positional argument: on Windows the whole
        // command line is capped at ~32K characters, and a long conversation or a large paste
        // blows past that, so CreateProcess fails with "the filename or extension is too long".
        // `-` tells codex to read the prompt from stdin instead.
        arguments.Add("-");

        return arguments;
    }

    private static string BuildPrompt(TurnRequest request) =>
        string.IsNullOrWhiteSpace(request.SystemPrompt)
            ? request.Prompt
            : $"{request.SystemPrompt}\n\n---\n\n{request.Prompt}";

    /// <summary>
    /// Hands codex the MCP servers this session is meant to have. Codex reads servers from its own
    /// config rather than from the .mcp.json the other CLIs take, so each one is passed as a config
    /// override instead. They merge with whatever is already in the user's config.toml.
    /// </summary>
    private IEnumerable<string> McpOverrides(string workspace)
    {
        var configPath = Path.Combine(workspace, ".mcp.json");
        if (!File.Exists(configPath))
            yield break;

        JsonObject? servers = null;
        try
        {
            servers = JsonNode.Parse(File.ReadAllText(configPath))?["mcpServers"] as JsonObject;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the MCP config at {Path}", configPath);
        }

        if (servers is null)
            yield break;

        foreach (var (name, entry) in servers)
        {
            if (entry is not JsonObject server)
                continue;

            // Remote servers are left to the user's own codex config: this app has not verified the
            // shape codex wants for them, and guessing would produce a server that never connects.
            if ((server["type"]?.GetValue<string>() ?? "stdio") != "stdio")
            {
                _logger.LogInformation(
                    "MCP server {Server} is not stdio, so it was not passed to codex", name);
                continue;
            }

            var key = $"mcp_servers.{TomlKey(name)}";

            yield return "-c";
            yield return $"{key}.command={TomlString(server["command"]?.GetValue<string>() ?? string.Empty)}";

            var args = (server["args"] as JsonArray ?? []).Select(a => TomlString(a?.GetValue<string>() ?? string.Empty));
            yield return "-c";
            yield return $"{key}.args=[{string.Join(", ", args)}]";

            if (server["env"] is JsonObject environment && environment.Count > 0)
            {
                var pairs = environment.Select(pair => $"{TomlKey(pair.Key)} = {TomlString(pair.Value?.GetValue<string>() ?? string.Empty)}");
                yield return "-c";
                yield return $"{key}.env={{{string.Join(", ", pairs)}}}";
            }
        }
    }

    /// <summary>The value half of an override is parsed as TOML, so a string has to look like one.</summary>
    private static string TomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// A server named in a way that is not a bare TOML key — a dot or a space — has to be quoted,
    /// or the dotted path would be read as another level of nesting.
    /// </summary>
    private static string TomlKey(string name) =>
        name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-') ? name : TomlString(name);

    private Dictionary<string, string> BuildEnvironment(TurnRequest request)
    {
        var home = request.HomeDirectory ?? _options.HomePath;
        var environment = new Dictionary<string, string>
        {
            // HOME and CODEX_HOME together are what select the logged-in account.
            ["HOME"] = home,
            ["CODEX_HOME"] = Path.Combine(home, ".codex"),
        };

        foreach (var pair in request.Environment)
            environment[pair.Key] = pair.Value;

        return environment;
    }

    /// <summary>
    /// Codex has changed its JSON event shape between releases, so this reads both the newer
    /// <c>item.*</c> events and the older <c>msg</c> envelope, and never throws on an unknown shape.
    /// </summary>
    private static IEnumerable<AgentEvent> Translate(string line, bool isError, MessageStream stream)
    {
        if (string.IsNullOrWhiteSpace(line))
            yield break;

        if (isError)
        {
            yield return AgentEvent.Log(line);
            yield break;
        }

        // Not every line is JSON (banners, warnings); those are kept as log lines.
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
        }

        if (document is null)
        {
            yield return AgentEvent.Log(line);
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;

            foreach (var key in new[] { "thread_id", "session_id", "conversation_id" })
            {
                if (root.TryGetProperty(key, out var id) && id.ValueKind == JsonValueKind.String)
                {
                    yield return new AgentEvent(AgentEventKind.SessionId, id.GetString()!);
                    break;
                }
            }

            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

            // Newer shape: {"type":"item.completed","item":{"item_type":"agent_message","text":"..."}}
            if (type is "item.completed" or "item.started" or "item.updated" && root.TryGetProperty("item", out var item))
            {
                // codex 0.145 names this "type"; older builds used "item_type". Reading only one
                // of them means the answer is parsed as an unknown event and never reaches the
                // transcript, which looks exactly like the model saying nothing.
                var itemType = item.TryGetProperty("type", out var it) || item.TryGetProperty("item_type", out it)
                    ? it.GetString()
                    : null;

                if (itemType == "agent_message")
                {
                    // item.updated carries the message so far, item.completed the whole of it.
                    // Both are cumulative, so only the part not yet shown is worth sending.
                    var chunk = stream.Advance(GetString(item, "text"));
                    if (type == "item.completed")
                        stream.Reset();

                    if (chunk.Length > 0)
                        yield return AgentEvent.Text_(chunk);
                }
                else if (itemType is "command_execution" && type == "item.started")
                {
                    yield return new AgentEvent(AgentEventKind.Tool, GetString(item, "command"))
                    {
                        ToolName = "shell",
                        ToolCallId = GetString(item, "id"),
                    };
                }
                else if (itemType is "command_execution" && type == "item.completed")
                {
                    // The command has run; the line it opened can now say how long it took.
                    yield return new AgentEvent(AgentEventKind.ToolCompleted, string.Empty)
                    {
                        ToolCallId = GetString(item, "id"),
                    };
                }
                else if (itemType is "file_change" or "patch_apply" && type == "item.completed")
                {
                    // codex reports the patch it applied when it has one, which is what the
                    // transcript shows rather than the bare path.
                    var patch = GetString(item, "unified_diff");
                    var path = GetString(item, "path");

                    yield return new AgentEvent(AgentEventKind.Tool, path)
                    {
                        ToolName = "edit",
                        Edit = path.Length == 0 ? null : new FileEdit(path, UnifiedDiff: patch.Length == 0 ? null : patch),
                    };
                }
                else if (itemType == "error")
                {
                    yield return AgentEvent.Error(GetString(item, "message"));
                }

                yield break;
            }

            // Older shape: {"msg":{"type":"agent_message","message":"..."}}
            if (root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                var msgType = msg.TryGetProperty("type", out var mt) ? mt.GetString() : null;
                switch (msgType)
                {
                    case "agent_message_delta":
                        // Deltas are already just the new text, so they bypass the cumulative
                        // bookkeeping and only record how much has gone out.
                        var delta = GetString(msg, "delta");
                        if (delta.Length > 0)
                        {
                            stream.Emitted(delta);
                            yield return AgentEvent.Text_(delta);
                        }
                        break;
                    case "agent_message":
                        var message = stream.Advance(GetString(msg, "message"));
                        stream.Reset();
                        if (message.Length > 0)
                            yield return AgentEvent.Text_(message);
                        break;
                    case "exec_command_begin":
                        yield return new AgentEvent(AgentEventKind.Tool, GetString(msg, "command"))
                        {
                            ToolName = "shell",
                            ToolCallId = GetString(msg, "call_id"),
                        };
                        break;
                    case "exec_command_end":
                        yield return new AgentEvent(AgentEventKind.ToolCompleted, string.Empty)
                        {
                            ToolCallId = GetString(msg, "call_id"),
                        };
                        break;
                    case "error":
                        yield return AgentEvent.Error(GetString(msg, "message"));
                        break;
                    case "token_count":
                        // The last request's usage, not the running total: the orchestrator adds
                        // these up itself, and totals would count every earlier request again.
                        if (ReadUsage(msg) is { IsEmpty: false } spent)
                            yield return new AgentEvent(AgentEventKind.Usage, string.Empty) { Usage = spent };
                        break;
                    case "task_complete":
                        yield return new AgentEvent(AgentEventKind.Result, GetString(msg, "last_agent_message"));
                        break;
                    default:
                        yield return AgentEvent.Log(line);
                        break;
                }

                yield break;
            }

            yield return AgentEvent.Log(line);
        }
    }

    /// <summary>
    /// Tracks how much of the message being written has already reached the transcript. Codex
    /// reports the text so far on every update and again in full when the message is done, so
    /// without this the answer would be repeated, growing, on every event.
    /// </summary>
    private sealed class MessageStream
    {
        private string _sent = string.Empty;

        /// <summary>The part of <paramref name="textSoFar"/> that has not been sent yet.</summary>
        public string Advance(string textSoFar)
        {
            if (string.IsNullOrEmpty(textSoFar))
                return string.Empty;

            // Normally each update extends the last. A rewrite is not something codex does, but if
            // it ever did, showing the new text whole beats showing a nonsense fragment of it.
            if (!textSoFar.StartsWith(_sent, StringComparison.Ordinal))
            {
                _sent = textSoFar;
                return textSoFar;
            }

            var chunk = textSoFar[_sent.Length..];
            _sent = textSoFar;
            return chunk;
        }

        /// <summary>Records text that went out on its own, without going through Advance.</summary>
        public void Emitted(string chunk) => _sent += chunk;

        /// <summary>Called when a message is finished; the next one starts from nothing.</summary>
        public void Reset() => _sent = string.Empty;
    }

    /// <summary>
    /// Tokens for the request that just finished. codex nests these under "info" in current builds
    /// and reported them flat in older ones, and counts cached input inside the input total, which
    /// is unpicked here so the two are not counted twice over.
    /// </summary>
    private static TokenUsage? ReadUsage(JsonElement msg)
    {
        var usage = msg;

        if (msg.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
            usage = info;

        if (usage.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
            usage = last;
        else if (usage.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
            usage = total;

        var cached = Number(usage, "cached_input_tokens");
        var input = Number(usage, "input_tokens");

        return new TokenUsage(Math.Max(0, input - cached), Number(usage, "output_tokens"), cached);
    }

    private static int Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Join(' ', value.EnumerateArray().Select(e => e.ToString())),
                JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
                _ => value.ToString(),
            };
        }

        return string.Empty;
    }

    public async Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default)
    {
        var home = homePath ?? _options.HomePath;
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["CODEX_HOME"] = Path.Combine(home, ".codex"),
        };

        var version = await _runner.RunAsync(
            new ProcessRequest(_options.CodexExecutable, ["--version"], TimeoutSeconds: 30, Environment: environment), ct);

        if (!version.Succeeded)
            return new ProviderAuthStatus(Provider, ProviderAuthState.Unknown, null, "The codex CLI is not installed.", DateTimeOffset.UtcNow);

        // `codex login status` is authoritative: it exits non-zero and says so when logged out.
        var status = await _runner.RunAsync(
            new ProcessRequest(_options.CodexExecutable, ["login", "status"], TimeoutSeconds: 30, Environment: environment),
            ct);

        var statusText = string.IsNullOrWhiteSpace(status.StandardOutput)
            ? status.StandardError.Trim()
            : status.StandardOutput.Trim();

        bool loggedIn;
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            loggedIn = !statusText.Contains("Not logged in", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Older builds have no status subcommand; fall back to the credentials file.
            loggedIn = File.Exists(Path.Combine(home, ".codex", "auth.json")) ||
                       System.Environment.GetEnvironmentVariable("OPENAI_API_KEY") is { Length: > 0 };
        }

        return new ProviderAuthStatus(
            Provider,
            loggedIn ? ProviderAuthState.LoggedIn : ProviderAuthState.LoggedOut,
            loggedIn ? statusText : null,
            version.Text,
            DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<LoginMethod> SupportedLoginMethods =>
        [LoginMethod.DeviceCode, LoginMethod.Browser, LoginMethod.Token];

    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.DeviceCode) =>
        method switch
        {
            // Redirects to a callback on port 1455 inside the container, so that port has to be
            // published and the browser has to be on the same machine.
            LoginMethod.Browser => (_options.CodexExecutable, (IReadOnlyList<string>)["login"]),
            // Reads the key from stdin; codex stores it, the orchestrator never does.
            LoginMethod.Token => (_options.CodexExecutable, (IReadOnlyList<string>)["login", "--with-api-key"]),
            // A link plus a short code — nothing needs to reach back into the container.
            _ => (_options.CodexExecutable, (IReadOnlyList<string>)["login", "--device-auth"]),
        };

    public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() =>
        (_options.CodexExecutable, ["logout"]);
}
