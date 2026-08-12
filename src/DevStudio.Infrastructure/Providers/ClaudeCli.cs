using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Mcp;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Providers;

/// <summary>
/// Drives the <c>claude</c> CLI in headless mode (<c>-p --output-format stream-json</c>), which is
/// the only integration point — the app never calls an Anthropic API directly, so the user's own
/// CLI login is the only credential in play.
/// </summary>
public sealed class ClaudeCli : IProviderCli
{
    /// <summary>Names the MCP server an event is about.</summary>
    private const string McpToolPrefix = "mcp:";

    /// <summary>
    /// What the init event says about a server that was not up when the CLI started. It is not a
    /// verdict: the CLI reconnects on first use often enough that treating it as an error cries
    /// wolf. The diagnosis that follows decides whether anything is actually wrong.
    /// </summary>
    private const string McpNotConnected = "did not connect at startup";

    private readonly IProcessRunner _runner;
    private readonly IMcpProbeService _mcpProbe;
    private readonly IEntityStore<McpServer> _mcpServers;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<ClaudeCli> _logger;

    public ClaudeCli(
        IProcessRunner runner,
        IMcpProbeService mcpProbe,
        IEntityStore<McpServer> mcpServers,
        IOptions<OrchestratorOptions> options,
        ILogger<ClaudeCli> logger)
    {
        _runner = runner;
        _mcpProbe = mcpProbe;
        _mcpServers = mcpServers;
        _options = options.Value;
        _logger = logger;
    }

    public AiProvider Provider => AiProvider.Claude;
    public string DisplayName => "Claude Code";

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        TurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var arguments = BuildArguments(request);

        // The CLI says only that a server failed, never why. Each failure starts a diagnosis that
        // connects from here and reports what the server actually said.
        var diagnoses = new ConcurrentBag<Task>();

        DisableBashSandbox(request.HomeDirectory ?? _options.HomePath);

        var pump = Task.Run(async () =>
        {
            try
            {
                var exitCode = await _runner.StreamAsync(
                    new ProcessRequest(
                        _options.ClaudeExecutable,
                        arguments,
                        request.WorkingDirectory,
                        BuildEnvironment(request),
                        TimeoutSeconds: 0),
                    async (line, isError, _) =>
                    {
                        if (line.Contains("bwrap:", StringComparison.Ordinal))
                        {
                            await channel.Writer.WriteAsync(AgentEvent.Error(
                                "The CLI could not build its bash sandbox, so the command never ran. " +
                                "That sandbox needs unprivileged user namespaces, which this host " +
                                "disables. Set Orchestrator__ContainerIsTheSandbox=true and restart, " +
                                "or allow namespaces with security_opt: seccomp:unconfined."), CancellationToken.None);
                        }

                        foreach (var evt in Translate(line, isError))
                        {
                            await channel.Writer.WriteAsync(evt, CancellationToken.None);

                            if (evt.Kind == AgentEventKind.Tool
                                && evt.Text == McpNotConnected
                                && evt.ToolName is { } tool
                                && tool.StartsWith(McpToolPrefix, StringComparison.Ordinal))
                            {
                                diagnoses.Add(DiagnoseMcpAsync(tool[McpToolPrefix.Length..], channel.Writer));
                            }
                        }
                    },
                    ct);

                if (exitCode != 0)
                {
                    await channel.Writer.WriteAsync(
                        AgentEvent.Error($"claude exited with code {exitCode}."), CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is surfaced by the session manager, not as a transcript error.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "claude turn failed");
                await channel.Writer.WriteAsync(AgentEvent.Error(ex.Message), CancellationToken.None);
            }
            finally
            {
                // A diagnosis outlives the line that triggered it, so the transcript stays open
                // until each one has had its say.
                try
                {
                    await Task.WhenAll(diagnoses);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Diagnosing a failed MCP server did not complete");
                }

                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var evt in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return evt;

        await pump;
    }

    private List<string> BuildArguments(TurnRequest request)
    {
        var arguments = new List<string>
        {
            "-p", request.Prompt,
            "--output-format", "stream-json",
            "--verbose",
            // Without this the CLI only reports a block of prose once it is finished, so a long
            // answer arrives in one lump. With it the same text also arrives as deltas, which is
            // what the transcript renders as the model types.
            "--include-partial-messages",
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model!);
        }

        if (!string.IsNullOrWhiteSpace(request.Effort))
        {
            arguments.Add("--effort");
            arguments.Add(request.Effort!);
        }

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            arguments.Add("--append-system-prompt");
            arguments.Add(request.SystemPrompt!);
        }

        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            arguments.Add("--resume");
            arguments.Add(request.ResumeSessionId!);
        }

        switch (request.PermissionMode)
        {
            case PermissionMode.Plan:
                arguments.Add("--permission-mode");
                arguments.Add("plan");
                break;
            case PermissionMode.AcceptEdits:
                arguments.Add("--permission-mode");
                arguments.Add("acceptEdits");
                break;
            case PermissionMode.Unrestricted:
                arguments.Add("--dangerously-skip-permissions");
                break;
            default:
                arguments.Add("--permission-mode");
                arguments.Add("default");
                break;
        }

        // Written by the workspace provisioner from the agent's selected MCP servers.
        var mcpConfig = Path.Combine(request.WorkingDirectory, ".mcp.json");
        if (File.Exists(mcpConfig))
        {
            arguments.Add("--mcp-config");
            arguments.Add(mcpConfig);
        }

        // Without this the CLI asks permission for every MCP tool and, with no one to answer,
        // the call simply fails — which reads to the model as "that server isn't available".
        // Rules the operator approved in the UI ride along on the same flag.
        if (request.McpServerNames.Count > 0 || request.AllowedTools.Count > 0)
        {
            arguments.Add("--allowedTools");
            foreach (var server in request.McpServerNames)
                arguments.Add($"mcp__{server}");
            foreach (var rule in request.AllowedTools)
                arguments.Add(rule);
        }

        if (!string.IsNullOrWhiteSpace(request.ExtraArguments))
            arguments.AddRange(SplitArguments(request.ExtraArguments!));

        return arguments;
    }

    /// <summary>
    /// Turns the CLI's own bash sandbox off in the account's settings.
    ///
    /// The environment variable covers the case where the sandbox is being forced on by a remote
    /// gate, but a settings file that asks for it explicitly beats the variable — and settings live
    /// in the home volume, which survives a rebuild. Both are needed, because the sandbox is
    /// bubblewrap: where unprivileged user namespaces are disabled it cannot create its namespace,
    /// and every command fails before it starts.
    ///
    /// Everything else in the file is left exactly as it was; only this one key is settled.
    /// </summary>
    private void DisableBashSandbox(string homePath)
    {
        if (!_options.ContainerIsTheSandbox)
            return;

        try
        {
            var directory = Path.Combine(homePath, ".claude");
            var path = Path.Combine(directory, "settings.json");

            var settings = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? []
                : [];

            if (settings["sandbox"] is not JsonObject sandbox)
                settings["sandbox"] = sandbox = [];

            if (sandbox["enabled"]?.GetValue<bool>() == false)
                return;

            sandbox["enabled"] = false;

            Directory.CreateDirectory(directory);
            File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("Turned the claude bash sandbox off in {Path}", path);
        }
        catch (Exception ex)
        {
            // Not fatal: the environment variable may well be enough on its own.
            _logger.LogWarning(ex, "Could not update the claude settings under {Home}", homePath);
        }
    }

    private Dictionary<string, string> BuildEnvironment(TurnRequest request)
    {
        var environment = new Dictionary<string, string>
        {
            // HOME is how an account is selected — the CLI reads its credentials from there.
            ["HOME"] = request.HomeDirectory ?? _options.HomePath,
            ["CLAUDE_CODE_NONINTERACTIVE"] = "1",
        };

        // The CLI otherwise wraps each command in bubblewrap, which cannot create its namespace
        // where unprivileged user namespaces are disabled: bwrap fails and the command dies before
        // it starts. IS_SANDBOX is the one the bash sandbox actually consults — it means "this is
        // already a sandbox, do not build another". CLAUDE_CODE_SANDBOXED rides along because it
        // suppresses the trust prompt, which has nobody to answer it either.
        if (_options.ContainerIsTheSandbox)
        {
            environment["IS_SANDBOX"] = "1";
            environment["CLAUDE_CODE_SANDBOXED"] = "1";
        }

        foreach (var pair in request.Environment)
            environment[pair.Key] = pair.Value;

        return environment;
    }

    /// <summary>Maps one line of the CLI's stream-json output onto transcript events.</summary>
    private static IEnumerable<AgentEvent> Translate(string line, bool isError)
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
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

            if (root.TryGetProperty("session_id", out var sessionId) && sessionId.ValueKind == JsonValueKind.String)
                yield return new AgentEvent(AgentEventKind.SessionId, sessionId.GetString()!);

            switch (type)
            {
                // The partial-message feed. Only text deltas matter here: tool calls still arrive
                // whole on the assistant event that follows.
                case "stream_event":
                    if (root.TryGetProperty("event", out var streamEvent) &&
                        streamEvent.TryGetProperty("type", out var streamType) &&
                        streamType.GetString() == "content_block_delta" &&
                        streamEvent.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("type", out var deltaType) &&
                        deltaType.GetString() == "text_delta" &&
                        delta.TryGetProperty("text", out var deltaText) &&
                        deltaText.GetString() is { Length: > 0 } chunk)
                    {
                        yield return AgentEvent.Text_(chunk);
                    }
                    break;

                case "assistant":
                    if (root.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                            if (blockType == "text")
                            {
                                // Deliberately dropped: --include-partial-messages already streamed
                                // this text delta by delta, and yielding the finished block as well
                                // would print the whole answer a second time.
                            }
                            else if (blockType == "tool_use")
                            {
                                var name = block.TryGetProperty("name", out var toolName) ? toolName.GetString() : "tool";
                                yield return new AgentEvent(AgentEventKind.Tool, DescribeToolInput(block))
                                {
                                    ToolName = name,
                                    ToolCallId = block.TryGetProperty("id", out var callId) ? callId.GetString() : null,
                                };
                            }
                        }
                    }
                    break;

                // A tool's result comes back as a user message naming the call it answers. Nothing
                // in it belongs in the transcript — the tool line is already there — but it is what
                // says the call has finished, and so how long it took.
                case "user":
                    if (root.TryGetProperty("message", out var answer) &&
                        answer.TryGetProperty("content", out var answered) &&
                        answered.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in answered.EnumerateArray())
                        {
                            if (block.TryGetProperty("type", out var kind) && kind.GetString() == "tool_result" &&
                                block.TryGetProperty("tool_use_id", out var answeredId) &&
                                answeredId.GetString() is { Length: > 0 } finished)
                            {
                                yield return new AgentEvent(AgentEventKind.ToolCompleted, string.Empty)
                                {
                                    ToolCallId = finished,
                                };
                            }
                        }
                    }

                    break;

                case "result":
                    // Headless, a permission prompt has nobody to answer it, so the CLI denies the
                    // call and lists what it refused. That list is the only way the operator ever
                    // learns a tool was blocked rather than simply not attempted.
                    if (root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var denial in denials.EnumerateArray())
                        {
                            var deniedTool = denial.TryGetProperty("tool_name", out var dt) ? dt.GetString() : null;
                            if (string.IsNullOrWhiteSpace(deniedTool))
                                continue;

                            yield return new AgentEvent(AgentEventKind.PermissionDenied, DescribeDeniedInput(denial))
                            {
                                ToolName = deniedTool,
                            };
                        }
                    }

                    var resultText = root.TryGetProperty("result", out var result) ? result.GetString() ?? string.Empty : string.Empty;
                    var cost = root.TryGetProperty("total_cost_usd", out var costElement) && costElement.TryGetDecimal(out var costValue)
                        ? costValue
                        : (decimal?)null;

                    if (root.TryGetProperty("is_error", out var isErrorElement) && isErrorElement.ValueKind == JsonValueKind.True)
                        yield return AgentEvent.Error(string.IsNullOrWhiteSpace(resultText) ? "The CLI reported an error." : resultText);
                    else
                        yield return new AgentEvent(AgentEventKind.Result, resultText) { CostUsd = cost };
                    break;

                case "system":
                    // The init event lists each MCP server and whether it actually connected. A
                    // failure here is invisible otherwise: the model simply has no such tools and
                    // says the service is not available.
                    if (root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var server in servers.EnumerateArray())
                        {
                            var serverName = server.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var serverStatus = server.TryGetProperty("status", out var st) ? st.GetString() : null;

                            if (string.IsNullOrWhiteSpace(serverName))
                                continue;

                            var connected = string.Equals(serverStatus, "connected", StringComparison.OrdinalIgnoreCase);

                            yield return new AgentEvent(AgentEventKind.Tool, connected ? "connected" : McpNotConnected)
                            {
                                ToolName = McpToolPrefix + serverName,
                            };
                        }
                    }

                    yield return AgentEvent.Log(line);
                    break;
            }
        }
    }

    /// <summary>
    /// Connects to a server the CLI could not reach at startup and puts the outcome in the
    /// transcript. This is what separates a server that is genuinely broken from one the CLI simply
    /// had not opened yet, and it is where the reason for a real failure comes from — "status:
    /// failed" is equally true of a bad URL, a rejected key and a server that is down.
    /// </summary>
    private async Task DiagnoseMcpAsync(string serverName, ChannelWriter<AgentEvent> writer)
    {
        try
        {
            var server = (await _mcpServers.GetAllAsync())
                .FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));

            if (server is null)
            {
                await writer.WriteAsync(AgentEvent.Log(
                    $"'{serverName}' is not registered in the orchestrator, so there is nothing to test it against."),
                    CancellationToken.None);
                return;
            }

            var probe = await _mcpProbe.ListToolsAsync(server);

            await writer.WriteAsync(
                probe.Succeeded
                    // Nothing is wrong with the server or the credential, so this stays a log line:
                    // the CLI opens these connections lazily and picks the server up on first use.
                    ? AgentEvent.Log($"'{serverName}' answered the orchestrator with the same settings ({probe.Detail}), so the CLI should reach it when it first calls a tool.")
                    : AgentEvent.Error($"'{serverName}' also refused the orchestrator: {probe.Detail} Its tools are unlikely to work this turn."),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diagnosing MCP server {Server} failed", serverName);
        }
    }

    /// <summary>
    /// What the denied call wanted to do. The command matters most — it is what the operator is
    /// being asked to approve — so it wins over the model's own description.
    /// </summary>
    private static string DescribeDeniedInput(JsonElement denial)
    {
        if (!denial.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (var key in new[] { "command", "file_path", "path", "url", "pattern", "description" })
        {
            if (input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        }

        return input.ToString();
    }

    private static string DescribeToolInput(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return string.Empty;

        // Show the field that identifies what the tool acted on, not the whole payload.
        foreach (var key in new[] { "command", "file_path", "path", "pattern", "url", "description", "prompt" })
        {
            if (input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? string.Empty;
                return text.Length <= 200 ? text : text[..197] + "...";
            }
        }

        return string.Empty;
    }

    public async Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default)
    {
        var home = homePath ?? _options.HomePath;

        var version = await _runner.RunAsync(
            new ProcessRequest(_options.ClaudeExecutable, ["--version"], TimeoutSeconds: 30,
                Environment: new Dictionary<string, string> { ["HOME"] = home }),
            ct);

        if (!version.Succeeded)
            return new ProviderAuthStatus(Provider, ProviderAuthState.Unknown, null, "The claude CLI is not installed.", DateTimeOffset.UtcNow);

        // `claude auth status` reports JSON and is authoritative; the credential file is only a
        // fallback for older CLI builds that lack the subcommand.
        var status = await _runner.RunAsync(
            new ProcessRequest(_options.ClaudeExecutable, ["auth", "status"], TimeoutSeconds: 30,
                Environment: new Dictionary<string, string> { ["HOME"] = home }),
            ct);

        bool? loggedIn = null;
        string? account = null;

        if (status.Succeeded)
        {
            try
            {
                using var document = JsonDocument.Parse(status.StandardOutput);
                var root = document.RootElement;

                if (root.TryGetProperty("loggedIn", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    loggedIn = flag.GetBoolean();

                foreach (var key in new[] { "email", "account", "authMethod" })
                {
                    if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        account = value.GetString();
                        break;
                    }
                }
            }
            catch (JsonException)
            {
                // Older builds print prose rather than JSON.
                loggedIn = status.StandardOutput.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
            }
        }

        loggedIn ??= File.Exists(Path.Combine(home, ".claude", ".credentials.json")) ||
                     File.Exists(Path.Combine(home, ".claude", "credentials.json")) ||
                     System.Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 };

        return new ProviderAuthStatus(
            Provider,
            loggedIn.Value ? ProviderAuthState.LoggedIn : ProviderAuthState.LoggedOut,
            loggedIn.Value ? account : null,
            version.Text,
            DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<LoginMethod> SupportedLoginMethods =>
        [LoginMethod.Browser, LoginMethod.Console, LoginMethod.LongLivedToken];

    // `claude auth login` prints the sign-in URL and waits for the code to be pasted back, which is
    // exactly what the browser terminal needs — far better than driving the full interactive UI.
    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
        method switch
        {
            // Anthropic Console account, billed by API usage, rather than a Claude subscription.
            LoginMethod.Console => (_options.ClaudeExecutable, (IReadOnlyList<string>)["auth", "login", "--console"]),
            // Mints a long-lived token through the browser — what unattended schedules want.
            LoginMethod.LongLivedToken => (_options.ClaudeExecutable, (IReadOnlyList<string>)["setup-token"]),
            _ => (_options.ClaudeExecutable, (IReadOnlyList<string>)["auth", "login"]),
        };

    public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() =>
        (_options.ClaudeExecutable, ["auth", "logout"]);

    /// <summary>Splits a raw argument string on whitespace, honouring quoted segments.</summary>
    internal static IEnumerable<string> SplitArguments(string value)
    {
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var ch in value)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                else
                    current.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}
