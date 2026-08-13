using DevStudio.Domain.Common;

namespace DevStudio.Domain.Teams;

/// <summary>
/// Which repository holds the team's shared setup, and what the last import of it did. A single
/// record — the store holds exactly one, under <see cref="WellKnownId"/>.
///
/// The point of it is that a team's agents, workflows, skills, schedules and standards belong in
/// review and in version control like any other code, instead of being re-entered by hand on every
/// machine. Anything created in the UI stays local and private to that install: the sync only ever
/// owns what the repository itself defines.
/// </summary>
public sealed class TeamSettings : Entity
{
    public const string WellKnownId = "team";

    /// <summary>Registered repository the definitions are read from. Null means the feature is off.</summary>
    public string? RepositoryId { get; set; }

    /// <summary>
    /// Folder inside the repository holding the definitions. Empty means its root — right for a
    /// repository that exists for this and nothing else.
    /// </summary>
    public string Folder { get; set; } = "devstudio";

    /// <summary>Fetch and fast-forward the checkout before reading it, so a sync is never stale.</summary>
    public bool PullBeforeSync { get; set; } = true;

    /// <summary>Import on every start, so a machine that has been off catches up by itself.</summary>
    public bool SyncOnStart { get; set; } = true;

    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>Why the last sync failed, or null when it did not.</summary>
    public string? LastError { get; set; }

    /// <summary>What the last sync did, line by line, for somebody working out why they got what they got.</summary>
    public List<string> LastLog { get; set; } = [];

    public TeamSettings() => Id = WellKnownId;
}
