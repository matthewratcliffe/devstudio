using DevStudio.Domain.Teams;

namespace DevStudio.Application.Teams;

/// <summary>What one import changed, for the page that asked for it to report honestly.</summary>
public sealed record TeamSyncResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<string> Log,
    TeamSyncCounts Counts)
{
    public static TeamSyncResult Failed(string message) =>
        new(false, message, [message], new TeamSyncCounts());
}

public sealed record TeamSyncCounts(
    int Skills = 0,
    int Agents = 0,
    int Workflows = 0,
    int Schedules = 0,
    int Removed = 0,
    bool Standards = false)
{
    public int Total => Skills + Agents + Workflows + Schedules;
}

/// <summary>
/// Imports the team's shared setup from a git repository: agents, workflows, skills, schedules and
/// standards. Definitions created in the UI are local to the install and are never touched — the two
/// sets live side by side, and only the repository's own are rewritten or removed by a sync.
/// </summary>
public interface ITeamSettingsService
{
    Task<TeamSettings> GetAsync(CancellationToken ct = default);

    Task<TeamSettings> SaveAsync(TeamSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Reads the configured repository and applies what it finds. Never throws for a bad repository or
    /// a malformed file: the failure is the result, because this also runs unattended at startup.
    /// </summary>
    Task<TeamSyncResult> SyncAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes the folder layout and one worked example of each kind into the repository, so a team
    /// starting from an empty repo has something to edit and commit rather than a format to guess at.
    /// Existing files are left alone.
    /// </summary>
    Task<TeamSyncResult> ScaffoldAsync(CancellationToken ct = default);
}
