using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Sessions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Common;
using DevStudio.Domain.Sessions;
using DevStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// The sessions list is a view of current work, so finished conversations fall into the archive on
/// their own once they are a day old — unless somebody has pulled one back out by hand.
/// </summary>
public class SessionAutoArchiveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "devstudio-archive-" + Guid.NewGuid().ToString("n"));

    private readonly JsonEntityStore<ChatSession> _store;

    public SessionAutoArchiveTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root });
        _store = new JsonEntityStore<ChatSession>(options, NullLogger<JsonEntityStore<ChatSession>>.Instance);
    }

    private SessionArchiver Archiver(int hours = 24)
    {
        var options = Options.Create(new OrchestratorOptions
        {
            DataPath = _root,
            HomePath = _root,
            SessionAutoArchiveHours = hours,
        });

        return new SessionArchiver(
            new StoreBackedSessions(_store),
            _store,
            options,
            NullLogger<SessionArchiver>.Instance);
    }

    private async Task<ChatSession> SeedAsync(
        SessionStatus status,
        TimeSpan age,
        bool archived = false,
        bool exempt = false)
    {
        var when = DateTimeOffset.UtcNow - age;

        var session = await _store.UpsertAsync(new ChatSession
        {
            Title = $"{status} {age.TotalHours:0}h",
            Status = status,
            EndedAt = status is SessionStatus.Completed or SessionStatus.Failed or SessionStatus.Cancelled
                ? when
                : null,
            IsArchived = archived,
            AutoArchiveExempt = exempt,
        });

        // The store stamps UpdatedAt on every write, so the age is applied afterwards — the store
        // hands back the same instance it caches, which is what the sweep then reads.
        session.UpdatedAt = when;
        return session;
    }

    [Fact]
    public async Task A_finished_session_older_than_the_cutoff_is_archived()
    {
        var session = await SeedAsync(SessionStatus.Completed, TimeSpan.FromHours(30));

        var archived = await Archiver().SweepAsync();

        Assert.Equal([session.Id], archived.Select(s => s.Id));
        Assert.True((await _store.GetAsync(session.Id))!.IsArchived);
    }

    [Fact]
    public async Task A_recent_session_is_left_alone()
    {
        var session = await SeedAsync(SessionStatus.Completed, TimeSpan.FromHours(2));

        Assert.Empty(await Archiver().SweepAsync());
        Assert.False((await _store.GetAsync(session.Id))!.IsArchived);
    }

    [Fact]
    public async Task A_session_still_running_is_never_archived_however_old_it_is()
    {
        var running = await SeedAsync(SessionStatus.Running, TimeSpan.FromDays(9));
        var waiting = await SeedAsync(SessionStatus.AwaitingInput, TimeSpan.FromDays(9));

        Assert.Empty(await Archiver().SweepAsync());
        Assert.False((await _store.GetAsync(running.Id))!.IsArchived);
        Assert.False((await _store.GetAsync(waiting.Id))!.IsArchived);
    }

    [Fact]
    public async Task A_session_restored_from_the_archive_is_never_taken_again()
    {
        var restored = await SeedAsync(SessionStatus.Completed, TimeSpan.FromDays(9), exempt: true);

        Assert.Empty(await Archiver().SweepAsync());
        Assert.False((await _store.GetAsync(restored.Id))!.IsArchived);
    }

    [Fact]
    public async Task Zero_hours_switches_the_sweep_off()
    {
        var session = await SeedAsync(SessionStatus.Completed, TimeSpan.FromDays(9));

        Assert.Empty(await Archiver(hours: 0).SweepAsync());
        Assert.False((await _store.GetAsync(session.Id))!.IsArchived);
    }

    /// <summary>Reads straight through to the store: nothing here has a live process behind it.</summary>
    private sealed class StoreBackedSessions : ISessionManager
    {
        private readonly IEntityStore<ChatSession> _store;

        public StoreBackedSessions(IEntityStore<ChatSession> store) => _store = store;

        public IReadOnlyList<ChatSession> Live => [];

        public event Action<ChatSession>? SessionUpdated
        {
            add { }
            remove { }
        }

        public async Task<IReadOnlyList<ChatSession>> GetAllAsync(CancellationToken ct = default) =>
            await _store.GetAllAsync(ct);

        public async Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct = default) =>
            await _store.GetAsync(sessionId, ct);

        public Task<ChatSession> StartAsync(StartSessionRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SendAsync(string sessionId, string message, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GuidanceMessage> SendGuidanceAsync(
            string sessionId,
            string guidance,
            string source = "operator",
            bool interrupt = false,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GuidanceMessage>> TakeGuidanceAsync(
            string sessionId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ToolApproval?> ResolveApprovalAsync(
            string sessionId,
            string approvalId,
            bool allow,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelAsync(string sessionId) => throw new NotSupportedException();

        public Task<ChatSession?> SetStatusAsync(
            string sessionId,
            SessionStatus status,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ChatSession?> CloseAsync(
            string sessionId,
            string? reason = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ChatSession> RunToCompletionAsync(
            StartSessionRequest request,
            TimeSpan timeout,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
