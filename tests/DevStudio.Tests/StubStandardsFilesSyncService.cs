using DevStudio.Application.Globals;

namespace DevStudio.Tests;

/// <summary>Does nothing — used everywhere a <see cref="WorkspaceService"/> under test needs one wired up.</summary>
internal sealed class StubStandardsFilesSyncService : IStandardsFilesSyncService
{
    public Task<StandardsSyncResult> SyncAsync(CancellationToken ct = default) =>
        Task.FromResult(new StandardsSyncResult(true, "not configured", [], 0, 0));
}
