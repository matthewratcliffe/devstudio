using System.Text.Json;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Providers;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.SourceControl;

/// <summary>Wraps the GitHub CLI. Agents share this login for private clones and pull requests.</summary>
public sealed class GitHubCli : ISourceControlCli
{
    private readonly IProcessRunner _runner;
    private readonly ISourceControlHosts _hosts;
    private readonly OrchestratorOptions _options;

    public GitHubCli(IProcessRunner runner, ISourceControlHosts hosts, IOptions<OrchestratorOptions> options)
    {
        _runner = runner;
        _hosts = hosts;
        _options = options.Value;
    }

    public SourceControlProvider Provider => SourceControlProvider.GitHub;
    public string DisplayName => Host == "github.com" ? "GitHub CLI" : $"GitHub CLI ({Host})";
    public string Executable => _options.GitHubCliExecutable;

    /// <summary>Host every command is aimed at; github.com unless changed on the Logins page.</summary>
    public string Host => _hosts.Get(Provider);

    public IReadOnlyList<LoginMethod> SupportedLoginMethods => [LoginMethod.DeviceCode, LoginMethod.Token];

    public async Task<ProviderAuthState> GetAuthStateAsync(CancellationToken ct = default)
    {
        var result = await RunRawAsync(["auth", "status", "--hostname", Host], null, ct);
        if (result.ExitCode == -1)
            return ProviderAuthState.Unknown;

        // gh exits non-zero when logged out and prints to stderr when logged in.
        return result.ExitCode == 0 ? ProviderAuthState.LoggedIn : ProviderAuthState.LoggedOut;
    }

    public async Task<string> GetAuthStatusTextAsync(CancellationToken ct = default)
    {
        var result = await RunRawAsync(["auth", "status", "--hostname", Host], null, ct);
        var text = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        return string.IsNullOrWhiteSpace(text) ? "The gh CLI is not installed." : text.Trim();
    }

    public async Task<IReadOnlyList<RemoteRepoSummary>> ListRepositoriesAsync(int limit = 50, CancellationToken ct = default)
    {
        var result = await RunRawAsync(
            ["repo", "list", "--limit", limit.ToString(), "--json", "nameWithOwner,description,isPrivate,url"],
            null,
            ct);

        if (result.ExitCode != 0)
            return [];

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return document.RootElement.EnumerateArray()
                .Select(e => new RemoteRepoSummary(
                    e.GetProperty("nameWithOwner").GetString() ?? string.Empty,
                    e.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                    e.TryGetProperty("isPrivate", out var p) && p.GetBoolean(),
                    (e.TryGetProperty("url", out var u) ? u.GetString() : null) ?? string.Empty))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

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
            // A personal access token on stdin; gh stores it in its own credential store.
            ? (Executable,
                (IReadOnlyList<string>)["auth", "login", "--hostname", Host, "--git-protocol", "https", "--with-token"])
            : (Executable,
                (IReadOnlyList<string>)["auth", "login", "--hostname", Host, "--git-protocol", "https", "--web", "--skip-ssh-key"]);

    public async Task<GitCommandOutcome> ConfigureGitCredentialsAsync(CancellationToken ct = default)
    {
        // gh ships its own wiring for this; it writes the helper into the container's .gitconfig.
        var result = await RunRawAsync(["auth", "setup-git", "--hostname", Host], null, ct);
        return new GitCommandOutcome(result.Succeeded, result.Text);
    }

    private Task<ProcessResult> RunRawAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct) =>
        _runner.RunAsync(
            new ProcessRequest(
                Executable,
                arguments,
                workingDirectory,
                new Dictionary<string, string>
                {
                    ["HOME"] = _options.HomePath,
                    ["GH_PROMPT_DISABLED"] = "1",
                    // Commands that take no --hostname still reach the right instance.
                    ["GH_HOST"] = Host,
                    ["NO_COLOR"] = "1",
                },
                TimeoutSeconds: 120),
            ct);
}
