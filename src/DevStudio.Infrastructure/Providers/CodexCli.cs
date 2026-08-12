using System.Text.Json;
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
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var arguments = BuildArguments(request);

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
                        TimeoutSeconds: 0),
                    async (line, isError, _) =>
                    {
                        foreach (var evt in Translate(line, isError))
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

        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            arguments.Add("resume");
            arguments.Add(request.ResumeSessionId!);
        }

        arguments.Add("--json");
        arguments.Add("--skip-git-repo-check");
        arguments.Add("--cd");
        arguments.Add(request.WorkingDirectory);

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
                arguments.Add("--sandbox");
                arguments.Add("read-only");
                break;
            case PermissionMode.AcceptEdits:
                arguments.Add("--sandbox");
                arguments.Add("workspace-write");
                break;
            case PermissionMode.Unrestricted:
                arguments.Add("--dangerously-bypass-approvals-and-sandbox");
                break;
        }

        if (!string.IsNullOrWhiteSpace(request.ExtraArguments))
            arguments.AddRange(ClaudeCli.SplitArguments(request.ExtraArguments!));

        // Codex takes the prompt as the trailing positional argument.
        var prompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
            ? request.Prompt
            : $"{request.SystemPrompt}\n\n---\n\n{request.Prompt}";
        arguments.Add(prompt);

        return arguments;
    }

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
            if (type is "item.completed" or "item.started" && root.TryGetProperty("item", out var item))
            {
                var itemType = item.TryGetProperty("item_type", out var it) ? it.GetString() : null;

                if (itemType == "agent_message" && type == "item.completed")
                {
                    yield return AgentEvent.Text_(GetString(item, "text"));
                }
                else if (itemType is "command_execution" && type == "item.started")
                {
                    yield return new AgentEvent(AgentEventKind.Tool, GetString(item, "command")) { ToolName = "shell" };
                }
                else if (itemType is "file_change" or "patch_apply" && type == "item.completed")
                {
                    yield return new AgentEvent(AgentEventKind.Tool, GetString(item, "path")) { ToolName = "edit" };
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
                    case "agent_message":
                        yield return AgentEvent.Text_(GetString(msg, "message"));
                        break;
                    case "exec_command_begin":
                        yield return new AgentEvent(AgentEventKind.Tool, GetString(msg, "command")) { ToolName = "shell" };
                        break;
                    case "error":
                        yield return AgentEvent.Error(GetString(msg, "message"));
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
