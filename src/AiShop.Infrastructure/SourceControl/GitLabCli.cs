using System.Text.Json;
using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Domain.Providers;
using Microsoft.Extensions.Options;

namespace AiShop.Infrastructure.SourceControl;

/// <summary>
/// Wraps the GitLab CLI. Same shape as the GitHub one: agents share the container's login, so a
/// single sign-in covers private clones and opening merge requests.
/// </summary>
public sealed class GitLabCli : ISourceControlCli
{
    private readonly IProcessRunner _runner;
    private readonly ISourceControlHosts _hosts;
    private readonly OrchestratorOptions _options;

    public GitLabCli(IProcessRunner runner, ISourceControlHosts hosts, IOptions<OrchestratorOptions> options)
    {
        _runner = runner;
        _hosts = hosts;
        _options = options.Value;
    }

    public SourceControlProvider Provider => SourceControlProvider.GitLab;
    public string DisplayName => Host == "gitlab.com" ? "GitLab CLI" : $"GitLab CLI ({Host})";
    public string Executable => _options.GitLabCliExecutable;

    /// <summary>Host every command is aimed at; gitlab.com unless changed on the Logins page.</summary>
    public string Host => _hosts.Get(Provider);

    public IReadOnlyList<LoginMethod> SupportedLoginMethods => [LoginMethod.Token, LoginMethod.Browser];

    public async Task<ProviderAuthState> GetAuthStateAsync(CancellationToken ct = default)
    {
        var result = await RunRawAsync(["auth", "status", "--hostname", Host], null, ct);
        if (result.ExitCode == -1)
            return ProviderAuthState.Unknown;

        var text = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;

        // glab exits zero even when signed out on some versions, so the text is the real signal.
        if (text.Contains("No token provided", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no api token", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderAuthState.LoggedOut;
        }

        return result.ExitCode == 0 ? ProviderAuthState.LoggedIn : ProviderAuthState.LoggedOut;
    }

    public async Task<string> GetAuthStatusTextAsync(CancellationToken ct = default)
    {
        var result = await RunRawAsync(["auth", "status", "--hostname", Host], null, ct);
        var text = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        return string.IsNullOrWhiteSpace(text) ? "The glab CLI is not installed." : text.Trim();
    }

    public async Task<IReadOnlyList<RemoteRepoSummary>> ListRepositoriesAsync(int limit = 50, CancellationToken ct = default)
    {
        var result = await RunRawAsync(
            ["repo", "list", "--per-page", limit.ToString(), "--output", "json"],
            null,
            ct);

        if (result.ExitCode != 0)
            return [];

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return document.RootElement.EnumerateArray()
                .Select(e => new RemoteRepoSummary(
                    Read(e, "path_with_namespace") ?? Read(e, "name") ?? string.Empty,
                    Read(e, "description") ?? string.Empty,
                    !string.Equals(Read(e, "visibility"), "public", StringComparison.OrdinalIgnoreCase),
                    Read(e, "http_url_to_repo") ?? Read(e, "web_url") ?? string.Empty))
                .Where(r => r.FullName.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Read(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public async Task<GitCommandOutcome> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var result = await RunRawAsync(arguments, workingDirectory, ct);
        return new GitCommandOutcome(result.Succeeded, result.Text);
    }

    public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.DeviceCode) =>
        method == LoginMethod.Token
            // --stdin takes the token directly instead of walking the interactive prompt.
            ? (Executable, (IReadOnlyList<string>)["auth", "login", "--hostname", Host, "--stdin"])
            : (Executable, (IReadOnlyList<string>)["auth", "login", "--hostname", Host, "--web"]);

    private Task<ProcessResult> RunRawAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct) =>
        _runner.RunAsync(
            new ProcessRequest(
                Executable,
                arguments,
                workingDirectory,
                new Dictionary<string, string>
                {
                    ["HOME"] = _options.HomePath,
                    ["NO_COLOR"] = "1",
                    ["GITLAB_CLI_NO_UPDATE_NOTIFIER"] = "1",
                    // Commands that take no --hostname still target the right instance.
                    ["GITLAB_HOST"] = Host,
                },
                TimeoutSeconds: 120),
            ct);
}
