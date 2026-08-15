using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;

namespace DevStudio.Application.Abstractions;

public sealed record GitCommandOutcome(bool Succeeded, string Output);

/// <summary>A directory inside an allowed host mount, as offered by the folder picker.</summary>
public sealed record LocalDirectoryEntry(string Name, string Path, bool IsGitRepository, bool IsRegistered);

/// <summary>
/// One level of the folder picker. <paramref name="Path"/> is null at the top, where the entries
/// are the configured roots themselves and there is nowhere further up to go.
/// </summary>
public sealed record LocalBrowseResult(string? Path, string? ParentPath, IReadOnlyList<LocalDirectoryEntry> Entries);

/// <summary>Clone/fetch repositories and cut the worktrees that keep concurrent agents apart.</summary>
public interface IGitService
{
    /// <summary>Clones into the repositories volume and returns the registered repository.</summary>
    Task<GitRepository> CloneAsync(
        string remoteUrl,
        string? name,
        SourceControlProvider sourceControl = SourceControlProvider.GitHub,
        CancellationToken ct = default);

    /// <summary>
    /// Host directories that may be browsed and attached, already normalised. Empty when no bind
    /// mount has been configured, which is what the UI keys off to explain the feature.
    /// </summary>
    IReadOnlyList<string> LocalRepositoryRoots { get; }

    /// <summary>
    /// Lists directories under an allowed root for the folder picker. Pass null for the top level.
    /// A path outside every root is rejected rather than listed.
    /// </summary>
    Task<LocalBrowseResult> BrowseLocalAsync(string? path, CancellationToken ct = default);

    /// <summary>
    /// Registers an existing checkout on a host mount, without cloning or copying it. The remote,
    /// forge and default branch are read from the repository itself.
    /// </summary>
    Task<GitRepository> AttachLocalAsync(string path, string? name, CancellationToken ct = default);

    /// <summary>
    /// Renames a registration. Nothing on disk moves — the name is a label and a prefix for future
    /// worktree folders, so an empty name falls back to the checkout's own folder name.
    /// </summary>
    Task<GitRepository> RenameAsync(GitRepository repository, string? name, CancellationToken ct = default);

    Task<GitCommandOutcome> FetchAsync(GitRepository repository, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListBranchesAsync(GitRepository repository, CancellationToken ct = default);

    Task<string> GetStatusAsync(string workingDirectory, CancellationToken ct = default);

    /// <summary>Creates a worktree on a new branch cut from <paramref name="baseBranch"/>.</summary>
    Task<Worktree> CreateWorktreeAsync(
        GitRepository repository,
        string branch,
        string? baseBranch,
        bool ephemeral,
        CancellationToken ct = default);

    Task<GitCommandOutcome> RemoveWorktreeAsync(
        GitRepository repository,
        Worktree worktree,
        CancellationToken ct = default);

    /// <summary>Runs an arbitrary git command in a working directory, for the repo console.</summary>
    Task<GitCommandOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default);
}
