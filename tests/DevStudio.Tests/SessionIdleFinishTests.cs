using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Sessions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Common;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;
using DevStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// A conversation nobody comes back to is finished on its own after a few hours, so the sessions
/// list is not a pile of half-finished chats that can still be restarted against a workspace which
/// has moved on since.
/// </summary>
public class SessionIdleFinishTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "devstudio-idle-" + Guid.NewGuid().ToString("n"));

    private readonly JsonEntityStore<ChatSession> _store;
    private readonly SessionManager _sessions;

    public SessionIdleFinishTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root });

        _store = new JsonEntityStore<ChatSession>(options, NullLogger<JsonEntityStore<ChatSession>>.Instance);
        _sessions = new SessionManager(
            _store,
            new JsonEntityStore<Agent>(options, NullLogger<JsonEntityStore<Agent>>.Instance),
            new StubRegistry(),
            new StubWorkspace(_root),
            new StubAccounts(_root),
            new JsonEntityStore<Domain.Projects.Project>(options, NullLogger<JsonEntityStore<Domain.Projects.Project>>.Instance),
            options,
            NullLogger<SessionManager>.Instance);
    }

    private SessionIdleCloser Closer(int hours = 4) =>
        new(_sessions,
            Options.Create(new OrchestratorOptions
            {
                DataPath = _root,
                HomePath = _root,
                SessionIdleFinishHours = hours,
            }),
            NullLogger<SessionIdleCloser>.Instance);

    /// <summary>A conversation whose last line was said <paramref name="quiet"/> ago.</summary>
    private async Task<ChatSession> SeedAsync(TimeSpan quiet, SessionStatus status = SessionStatus.AwaitingInput)
    {
        var when = DateTimeOffset.UtcNow - quiet;

        var session = await _store.UpsertAsync(new ChatSession
        {
            Title = $"quiet for {quiet.TotalHours:0}h",
            Status = status,
            EndedAt = when,
            Messages = [new ChatMessage { Role = MessageRole.Agent, Content = "done", Timestamp = when }],
        });

        // The store stamps UpdatedAt on every write, so the age is applied afterwards.
        session.UpdatedAt = when;
        return session;
    }

    [Fact]
    public async Task A_conversation_quiet_for_longer_than_the_window_is_finished()
    {
        var session = await SeedAsync(TimeSpan.FromHours(5));

        var finished = await Closer().SweepAsync();

        Assert.Equal([session.Id], finished.Select(s => s.Id));

        var stored = (await _store.GetAsync(session.Id))!;
        Assert.True(stored.IsClosed);
        Assert.False(stored.AcceptsInput);
        Assert.Equal("No input for 4 hours.", stored.ClosedReason);
    }

    [Fact]
    public async Task A_conversation_spoken_to_recently_is_left_open()
    {
        var session = await SeedAsync(TimeSpan.FromHours(3));

        Assert.Empty(await Closer().SweepAsync());
        Assert.True((await _store.GetAsync(session.Id))!.AcceptsInput);
    }

    [Fact]
    public async Task An_agent_still_at_work_is_not_idle_however_long_it_has_been_running()
    {
        var running = await SeedAsync(TimeSpan.FromDays(2), SessionStatus.Running);

        Assert.Empty(await Closer().SweepAsync());
        Assert.False((await _store.GetAsync(running.Id))!.IsClosed);
    }

    [Fact]
    public async Task A_conversation_already_finished_is_not_finished_twice()
    {
        var session = await SeedAsync(TimeSpan.FromDays(2));
        await _sessions.CloseAsync(session.Id, "Ended from the chat.");

        Assert.Empty(await Closer().SweepAsync());

        // The reason it was actually ended for is not overwritten by the sweep's.
        Assert.Equal("Ended from the chat.", (await _store.GetAsync(session.Id))!.ClosedReason);
    }

    [Fact]
    public async Task Zero_hours_leaves_conversations_open_until_somebody_ends_them()
    {
        var session = await SeedAsync(TimeSpan.FromDays(9));

        Assert.Empty(await Closer(hours: 0).SweepAsync());
        Assert.True((await _store.GetAsync(session.Id))!.AcceptsInput);
    }

    [Fact]
    public async Task Housekeeping_does_not_count_as_somebody_being_there()
    {
        var session = await SeedAsync(TimeSpan.FromHours(9));

        // Saving a note touches the session without anything being said in it.
        session.Notes = "look at this later";
        await _store.UpsertAsync(session);

        Assert.Single(await Closer().SweepAsync());
    }

    private sealed class StubRegistry : IProviderCliRegistry
    {
        public IReadOnlyList<IProviderCli> All => [];
        public IProviderCli Get(AiProvider provider) => throw new NotSupportedException();

        public Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IProviderCli>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IProviderCli>>([]);
    }

    private sealed class StubWorkspace(string root) : IWorkspaceService
    {
        public Task<SessionWorkspace> PrepareAsync(Agent agent, string sessionId, string? projectId, CancellationToken ct = default)
        {
            Directory.CreateDirectory(root);
            return Task.FromResult(new SessionWorkspace(root, null, null, projectId));
        }

        public Task<SessionWorkspace> PrepareAsync(
            Agent agent,
            string sessionId,
            string? projectId,
            IReadOnlyList<string>? extraServerIds,
            CancellationToken ct = default) =>
            PrepareAsync(agent, sessionId, projectId, ct);

        public Task ReleaseAsync(SessionWorkspace workspace, CancellationToken ct = default) => Task.CompletedTask;
        public Task MaterialiseSkillsAsync(Agent agent, string workspacePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> MaterialiseMcpAsync(
            Agent agent,
            string workspacePath,
            IReadOnlyList<string>? extraServerIds = null,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task MaterialiseProjectFilesAsync(string projectId, string workspacePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task MaterialiseGlobalFilesAsync(string workspacePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> ComposeSystemPromptAsync(Agent agent, string? projectId, string? sessionId = null, TokenTactics tactics = TokenTactics.None, string? handoverModel = null, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task WriteGuidanceAsync(string workspacePath, IEnumerable<GuidanceMessage> guidance, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubAccounts(string home) : IAccountService
    {
        public Task<ResolvedAccount> ResolveAsync(Agent agent, string? projectId, CancellationToken ct = default) =>
            Task.FromResult(new ResolvedAccount(null, "stub", home));

        public Task<string> GetHomePathAsync(string accountId, CancellationToken ct = default) => Task.FromResult(home);

        public Task<ProviderAccount> CreateAsync(string name, AiProvider provider, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProviderAccount> CreateAsync(string name, AiProvider provider, string? cliProviderId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string accountId, bool deleteCredentials, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<ProviderAccount>> RefreshAuthStateAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>([]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
