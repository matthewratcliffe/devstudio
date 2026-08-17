using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Providers;

/// <summary>
/// Drives the OpenCoder npm CLI in prompt mode. OpenCoder is primarily an interactive TUI; the
/// prompt flag is the automation entry point and leaves the model/tool configuration in the
/// user's OpenCoder config file.
/// </summary>
public sealed class OpencoderCli : IProviderCli
{
    private readonly IProcessRunner _runner;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<OpencoderCli> _logger;

    public OpencoderCli(IProcessRunner runner, IOptions<OrchestratorOptions> options, ILogger<OpencoderCli> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public AiProvider Provider => AiProvider.Opencoder;
    public string DisplayName => "OpenCoder";
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
                        _options.OpencoderExecutable,
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
                    await channel.Writer.WriteAsync(AgentEvent.Error($"opencoder exited with code {exitCode}."), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "opencoder turn failed");
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

        var arguments = new List<string> { "--prompt", prompt, "--cwd", request.WorkingDirectory };
        if (!string.IsNullOrWhiteSpace(request.Model))
            arguments.AddRange(["--model", request.Model]);
        if (!string.IsNullOrWhiteSpace(request.ExtraArguments))
            arguments.AddRange(ClaudeCli.SplitArguments(request.ExtraArguments));
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
            _options.OpencoderExecutable, ["--version"], TimeoutSeconds: 30,
            Environment: new Dictionary<string, string>
            {
                ["HOME"] = homePath ?? _options.HomePath,
                ["NO_COLOR"] = "1",
            }), ct);

        return new ProviderAuthStatus(
            Provider,
            result.ExitCode == -1 ? ProviderAuthState.Unknown : ProviderAuthState.LoggedIn,
            null,
            result.ExitCode == -1 ? $"'{_options.OpencoderExecutable}' is not installed." : FirstLine(result.StandardOutput),
            DateTimeOffset.UtcNow);
    }

    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) => (string.Empty, []);
    public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() => (string.Empty, []);

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Installed; authentication is configured by OpenCoder.";
}
