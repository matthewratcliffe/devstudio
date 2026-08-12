using DevStudio.Domain.Providers;
using DevStudio.Domain.Repositories;

namespace DevStudio.Application.Abstractions;

public sealed record GitCommandOutcome(bool Succeeded, string Output);

/// <summary>Clone/fetch repositories and cut the worktrees that keep concurrent agents apart.</summary>
public interface IGitService
{
    /// <summary>Clones into the repositories volume and returns the registered repository.</summary>
    Task<GitRepository> CloneAsync(
        string remoteUrl,
        string? name,
        SourceControlProvider sourceControl = SourceControlProvider.GitHub,
        CancellationToken ct = default);

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
