using System.Text.Json;
using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Globals;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Providers;

/// <summary>
/// Drives any CLI described by a <see cref="CliProvider"/> definition. This is what lets a
/// subscription-backed tool you already have — Copilot, Gemini, whatever — become an agent without
/// a code change.
/// </summary>
public sealed class CustomCli : IProviderCli
{
    private readonly CliProvider _definition;
    private readonly IProcessRunner _runner;
    private readonly OrchestratorOptions _options;
    private readonly ILogger _logger;
    private readonly ISharedEnvironment? _shared;

    public CustomCli(
        CliProvider definition,
        IProcessRunner runner,
        OrchestratorOptions options,
        ILogger logger,
        ISharedEnvironment? shared = null)
    {
        _definition = definition;
        _runner = runner;
        _options = options;
        _logger = logger;
        _shared = shared;
    }

    public AiProvider Provider => AiProvider.Custom;
    public string DisplayName => _definition.Name;
    public string DefinitionId => _definition.Id;

    public IReadOnlyList<LoginMethod> SupportedLoginMethods =>
        string.IsNullOrWhiteSpace(_definition.LoginArguments) ? [] : [LoginMethod.Browser];

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        TurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var arguments = BuildArguments(request);

        // Resolved before the pump starts so the variables are settled for the whole turn.
        var environment = BuildEnvironment(request, await SharedAsync(ct));

        var pump = Task.Run(async () =>
        {
            try
            {
                var exitCode = await _runner.StreamAsync(
                    new ProcessRequest(
                        _definition.Executable,
                        arguments,
                        request.WorkingDirectory,
                        environment,
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
                        AgentEvent.Error($"{_definition.Executable} exited with code {exitCode}."), CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Cli} turn failed", _definition.Name);
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

    internal List<string> BuildArguments(TurnRequest request)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = ComposePrompt(request),
            ["systemPrompt"] = request.SystemPrompt ?? string.Empty,
            ["model"] = request.Model ?? string.Empty,
            ["effort"] = request.Effort ?? string.Empty,
            ["workdir"] = request.WorkingDirectory,
            ["sessionId"] = request.ResumeSessionId ?? string.Empty,
        };

        var arguments = new List<string>();

        // Resume first: most CLIs want the subcommand before anything else.
        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId) && !string.IsNullOrWhiteSpace(_definition.ResumeArguments))
            arguments.AddRange(Expand(_definition.ResumeArguments, values));

        if (!string.IsNullOrWhiteSpace(request.Model) && !string.IsNullOrWhiteSpace(_definition.ModelArguments))
            arguments.AddRange(Expand(_definition.ModelArguments, values));

        if (!string.IsNullOrWhiteSpace(request.Effort) && !string.IsNullOrWhiteSpace(_definition.EffortArguments))
            arguments.AddRange(Expand(_definition.EffortArguments, values));

        if (_definition.PermissionArguments.TryGetValue(request.PermissionMode.ToString(), out var permission) &&
            !string.IsNullOrWhiteSpace(permission))
        {
            arguments.AddRange(Expand(permission, values));
        }

        arguments.AddRange(Expand(_definition.PromptArguments, values));

        if (!string.IsNullOrWhiteSpace(request.ExtraArguments))
            arguments.AddRange(ClaudeCli.SplitArguments(request.ExtraArguments!));

        return arguments;
    }

    /// <summary>
    /// Splits first, substitutes second: a prompt with spaces has to stay one argument. An argument
    /// whose placeholder resolves to nothing is dropped, which is how optional flags work.
    /// </summary>
    private static IEnumerable<string> Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        foreach (var token in ClaudeCli.SplitArguments(template))
        {
            var placeholders = TemplateRenderer.FindPlaceholders(token);
            if (placeholders.Count > 0 &&
                placeholders.Any(p => !values.TryGetValue(p, out var value) || string.IsNullOrEmpty(value)))
            {
                continue;
            }

            yield return TemplateRenderer.Render(token, values);
        }
    }

    /// <summary>Folds the system prompt into the prompt when the definition has nowhere to put it.</summary>
    private string ComposePrompt(TurnRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SystemPrompt))
            return request.Prompt;

        var mentionsSystemPrompt =
            TemplateRenderer.FindPlaceholders(_definition.PromptArguments)
                .Concat(TemplateRenderer.FindPlaceholders(_definition.ResumeArguments))
                .Any(p => string.Equals(p, "systemPrompt", StringComparison.OrdinalIgnoreCase));

        return mentionsSystemPrompt
            ? request.Prompt
            : $"{request.SystemPrompt}\n\n---\n\n{request.Prompt}";
    }

    /// <summary>
    /// The install-wide variables, or none when nothing supplies them — the dependency is optional
    /// so a CLI constructed directly, as the tests do, needs no settings store behind it.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string>> SharedAsync(CancellationToken ct) =>
        _shared is null
            ? new Dictionary<string, string>()
            : await _shared.ForLocalAsync(ct);

    internal Dictionary<string, string> BuildEnvironment(
        TurnRequest request,
        IReadOnlyDictionary<string, string> shared)
    {
        // Shared variables go down first, under the definition's own and then the turn's: the
        // narrower the scope, the later it is applied and the more it wins.
        var environment = new Dictionary<string, string>(shared)
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

    internal IEnumerable<AgentEvent> Translate(string line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
            yield break;

        if (isError)
        {
            yield return AgentEvent.Log(line);
            yield break;
        }

        if (_definition.OutputFormat == CliOutputFormat.PlainText)
        {
            // A plain newline, not the platform's — the transcript is text shown in a browser.
            yield return AgentEvent.Text_(line + "\n");
            yield break;
        }

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
            // A non-JSON line in a JSON stream is usually a banner, not an answer.
            yield return AgentEvent.Log(line);
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;

            if (Read(root, _definition.SessionIdProperty) is { Length: > 0 } sessionId)
                yield return new AgentEvent(AgentEventKind.SessionId, sessionId);

            if (Read(root, _definition.ErrorProperty) is { Length: > 0 } error)
            {
                yield return AgentEvent.Error(error);
                yield break;
            }

            if (Read(root, _definition.TextProperty) is { Length: > 0 } text)
                yield return AgentEvent.Text_(text);
        }
    }

    /// <summary>Reads a property, following a dotted path when one is given.</summary>
    private static string? Read(JsonElement element, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var current = element;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                return null;

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.ToString(),
            JsonValueKind.Array => string.Concat(current.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())),
            _ => null,
        };
    }

    public async Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default)
    {
        var home = homePath ?? _options.HomePath;
        var environment = new Dictionary<string, string> { ["HOME"] = home, ["NO_COLOR"] = "1" };

        foreach (var pair in _definition.Environment)
            environment[pair.Key] = pair.Value;

        if (string.IsNullOrWhiteSpace(_definition.StatusArguments))
        {
            // No status command configured, so the best we can say is whether the CLI exists.
            var probe = await _runner.RunAsync(
                new ProcessRequest(_definition.Executable, ["--version"], TimeoutSeconds: 20, Environment: environment), ct);

            return new ProviderAuthStatus(
                Provider,
                probe.Succeeded ? ProviderAuthState.Unknown : ProviderAuthState.Unknown,
                null,
                probe.Succeeded ? "Installed. Add a status command to report sign-in state." : $"'{_definition.Executable}' is not installed.",
                DateTimeOffset.UtcNow);
        }

        var status = await _runner.RunAsync(
            new ProcessRequest(
                _definition.Executable,
                ClaudeCli.SplitArguments(_definition.StatusArguments).ToList(),
                TimeoutSeconds: 30,
                Environment: environment),
            ct);

        var text = string.IsNullOrWhiteSpace(status.StandardOutput) ? status.StandardError.Trim() : status.StandardOutput.Trim();

        if (status.ExitCode == -1)
            return new ProviderAuthStatus(Provider, ProviderAuthState.Unknown, null, $"'{_definition.Executable}' is not installed.", DateTimeOffset.UtcNow);

        var loggedIn = string.IsNullOrWhiteSpace(_definition.LoggedOutMarker)
            ? status.Succeeded
            : !text.Contains(_definition.LoggedOutMarker, StringComparison.OrdinalIgnoreCase);

        return new ProviderAuthStatus(
            Provider,
            loggedIn ? ProviderAuthState.LoggedIn : ProviderAuthState.LoggedOut,
            loggedIn ? FirstLine(text) : null,
            FirstLine(text),
            DateTimeOffset.UtcNow);
    }

    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
        (_definition.Executable, ClaudeCli.SplitArguments(_definition.LoginArguments).ToList());

    public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() =>
        (_definition.Executable, ClaudeCli.SplitArguments(_definition.LogoutArguments).ToList());

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
}
