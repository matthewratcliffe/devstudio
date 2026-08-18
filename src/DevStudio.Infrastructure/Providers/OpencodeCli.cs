using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Providers;

/// <summary>
/// Drives the opencode CLI through <c>opencode run</c>, its non-interactive entry point. opencode
/// is primarily an interactive TUI; <c>run</c> takes the prompt as a positional argument and
/// leaves the rest of the model/tool configuration in the user's opencode config file. Unlike
/// Claude Code or Codex, opencode holds no separate per-account login of its own to check or
/// manage here — whatever credentials its own config already has are what a turn runs with.
/// </summary>
public sealed class OpencodeCli : IProviderCli
{
    private readonly IProcessRunner _runner;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<OpencodeCli> _logger;

    public OpencodeCli(IProcessRunner runner, IOptions<OrchestratorOptions> options, ILogger<OpencodeCli> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public AiProvider Provider => AiProvider.Opencode;
    public string DisplayName => "OpenCode";
    public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
        TurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var pump = Task.Run(async () =>
        {
            try
            {
                var exitCode = await _runner.StreamAsync(
                    new ProcessRequest(
                        _options.OpencodeExecutable,
                        BuildArguments(request),
                        request.WorkingDirectory,
                        BuildEnvironment(request),
                        TimeoutSeconds: 0),
                    async (line, isError, _) =>
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            await channel.Writer.WriteAsync(isError ? AgentEvent.Log(line) : AgentEvent.Text_(line + "\n"), CancellationToken.None);
                    },
                    ct);

                if (exitCode != 0)
                    await channel.Writer.WriteAsync(AgentEvent.Error($"opencode exited with code {exitCode}."), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "opencode turn failed");
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
        var prompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
            ? request.Prompt
            : $"{request.SystemPrompt}\n\n---\n\n{request.Prompt}";

        // The working directory travels as the process's cwd (set on the ProcessRequest below), not
        // as a flag: opencode has none, it just resolves the project from where it was launched.
        var arguments = new List<string> { "run" };
        if (!string.IsNullOrWhiteSpace(request.Model))
            arguments.AddRange(["--model", request.Model]);
        if (!string.IsNullOrWhiteSpace(request.ExtraArguments))
            arguments.AddRange(ClaudeCli.SplitArguments(request.ExtraArguments));

        // The prompt is the last positional argument, so any extra arguments above it are still
        // read as flags rather than being swallowed as further positionals.
        arguments.Add(prompt);
        return arguments;
    }

    private static Dictionary<string, string> BuildEnvironment(TurnRequest request)
    {
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = request.HomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["NO_COLOR"] = "1",
        };
        foreach (var pair in request.Environment)
            environment[pair.Key] = pair.Value;
        return environment;
    }

    public async Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(new ProcessRequest(
            _options.OpencodeExecutable, ["--version"], TimeoutSeconds: 30,
            Environment: new Dictionary<string, string>
            {
                ["HOME"] = homePath ?? _options.HomePath,
                ["NO_COLOR"] = "1",
            }), ct);

        // opencode has no separate login state for this app to report: it is either installed and
        // ready to run, or it is not. There is no logged-out state to distinguish here.
        return new ProviderAuthStatus(
            Provider,
            result.ExitCode == -1 ? ProviderAuthState.Unknown : ProviderAuthState.LoggedIn,
            null,
            result.ExitCode == -1 ? $"'{_options.OpencodeExecutable}' is not installed." : FirstLine(result.StandardOutput),
            DateTimeOffset.UtcNow);
    }

    // No login/logout to drive: opencode needs no local auth flow through this app.
    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) => (string.Empty, []);
    public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() => (string.Empty, []);

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Installed; opencode needs no separate login.";
}
