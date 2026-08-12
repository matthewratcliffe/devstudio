using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;

namespace DevStudio.Application.Abstractions;

public sealed record RemoteRepoSummary(string FullName, string Description, bool IsPrivate, string CloneUrl);

/// <summary>
/// A forge CLI — <c>gh</c> or <c>glab</c>. Agents get the same binary on their PATH and share the
/// login, so one sign-in covers cloning private repos and opening merge or pull requests.
/// </summary>
public interface ISourceControlCli
{
    SourceControlProvider Provider { get; }

    string DisplayName { get; }

    /// <summary>Executable name, shown in the UI so it is obvious what is being run.</summary>
    string Executable { get; }

    /// <summary>Host this CLI targets. Configurable, for self-managed instances.</summary>
    string Host { get; }

    Task<ProviderAuthState> GetAuthStateAsync(CancellationToken ct = default);

    Task<string> GetAuthStatusTextAsync(CancellationToken ct = default);

    /// <summary>Repositories the signed-in account can clone.</summary>
    Task<IReadOnlyList<RemoteRepoSummary>> ListRepositoriesAsync(int limit = 50, CancellationToken ct = default);

    Task<GitCommandOutcome> RunAsync(IReadOnlyList<string> arguments, string? workingDirectory = null, CancellationToken ct = default);

    IReadOnlyList<LoginMethod> SupportedLoginMethods { get; }

    (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.DeviceCode);
}

/// <summary>Resolves the CLI for a forge.</summary>
public interface ISourceControlRegistry
{
    IReadOnlyList<ISourceControlCli> All { get; }

    ISourceControlCli Get(SourceControlProvider provider);
}
