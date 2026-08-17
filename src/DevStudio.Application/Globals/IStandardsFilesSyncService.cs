namespace DevStudio.Application.Globals;

/// <summary>What one sync of the standards files repository changed.</summary>
public sealed record StandardsSyncResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<string> Log,
    int Imported,
    int Removed)
{
    public static StandardsSyncResult Failed(string message) => new(false, message, [message], 0, 0);
}

/// <summary>
/// Imports reference files for <see cref="DevStudio.Domain.Globals.GlobalSettings"/> from a git
/// repository, the same way <c>ITeamSettingsService</c> imports agents and skills. Files uploaded by
/// hand are never touched — only files this sync itself wrote are updated or removed on a later sync.
/// </summary>
public interface IStandardsFilesSyncService
{
    /// <summary>
    /// Reads the configured repository and folder and applies what it finds. Never throws for a bad
    /// repository, a failed pull or an unreadable file: the failure is the result, because this also
    /// runs unattended at startup and before every new conversation.
    /// </summary>
    Task<StandardsSyncResult> SyncAsync(CancellationToken ct = default);
}
