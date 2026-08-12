using DevStudio.Domain.Common;
using DevStudio.Domain.Providers;

namespace DevStudio.Domain.Repositories;

/// <summary>A repo cloned into the persistent volume, plus the worktrees cut from it.</summary>
public sealed class GitRepository : Entity
{
    public string Name { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;

    /// <summary>Forge this clone came from, so the right CLI is used for it.</summary>
    public SourceControlProvider SourceControl { get; set; } = SourceControlProvider.GitHub;
    /// <summary>Absolute path of the primary clone inside the container volume.</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>
    /// Attached from a host bind mount rather than cloned by this app. The checkout belongs to
    /// whoever mounted it — an IDE on the host is usually editing it at the same time — so it is
    /// never cloned, never deleted, and its worktrees are cut on the mount where that IDE can see
    /// them.
    /// </summary>
    public bool IsLocal { get; set; }
    public string DefaultBranch { get; set; } = "main";
    public DateTimeOffset? LastFetchedAt { get; set; }
    public string? LastError { get; set; }
    public List<Worktree> Worktrees { get; set; } = [];
}

public sealed class Worktree
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Branch { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Session currently using this worktree, if any.</summary>
    public string? SessionId { get; set; }
    /// <summary>Created automatically for a session, so it can be pruned automatically too.</summary>
    public bool IsEphemeral { get; set; }
}
