using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Sessions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Common;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;
using DevStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// A conversation that belonged to some other piece of work — a queue item — is closed when that
/// work finishes. It keeps its transcript and takes no more turns.
/// </summary>
public class ClosedSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-closed-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<Agent> _agents;
    private readonly JsonEntityStore<ChatSession> _store;
    private readonly SessionManager _sessions;

    public ClosedSessionTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root, MaxConcurrentSessions = 1 });
        _agents = Store<Agent>(options);
        _store = Store<ChatSession>(options);

        _sessions = new SessionManager(
            _store,
            _agents,
            new StubRegistry(),
            new StubWorkspace(_root),
            new StubAccounts(_root),
            Store<Project>(options),
            options,
            NullLogger<SessionManager>.Instance);
    }

    private static JsonEntityStore<T> Store<T>(IOptions<OrchestratorOptions> options) where T : class, IEntity =>
        new(options, NullLogger<JsonEntityStore<T>>.Instance);

    private async Task<ChatSession> RunAsync()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Queue worker" });

        return await _sessions.RunToCompletionAsync(
            new StartSessionRequest
            {
                AgentId = agent.Id,
                Prompt = "Deal with mr-42.",
                Trigger = SessionTrigger.Queue,
                QueueItemId = "item-1",
            },
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Closing_a_session_settles_it_as_completed_and_read_only()
    {
        var session = await RunAsync();
        Assert.True(session.AcceptsInput);

        var closed = await _sessions.CloseAsync(session.Id, "Finished the queue item on Merge requests.");

        Assert.NotNull(closed);
        Assert.True(closed!.IsClosed);
        Assert.False(closed.AcceptsInput);
        Assert.Equal(SessionStatus.Completed, closed.Status);
        Assert.Equal("Finished the queue item on Merge requests.", closed.ClosedReason);
        Assert.NotNull(closed.ClosedAt);
        Assert.NotNull(closed.EndedAt);

        // It is the stored session that the chat page reads back, not just the live copy.
        var stored = await _store.GetAsync(session.Id);
        Assert.True(stored!.IsClosed);
    }

    [Fact]
    public async Task A_closed_session_refuses_another_turn()
    {
        var session = await RunAsync();
        await _sessions.CloseAsync(session.Id, "Finished the queue item on Merge requests.");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sessions.SendAsync(session.Id, "one more thing"));

        Assert.Contains("read only", thrown.Message);
        Assert.Contains("Merge requests", thrown.Message);
        // Your message and the answer to it, which is what one exchange counts as.
        Assert.Equal(2, (await _store.GetAsync(session.Id))!.TurnCount);
    }

    [Fact]
    public async Task A_closed_session_refuses_guidance_as_well()
    {
        var session = await RunAsync();
        await _sessions.CloseAsync(session.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sessions.SendGuidanceAsync(session.Id, "try the other branch"));
    }

    [Fact]
    public async Task Closing_a_run_that_failed_leaves_it_saying_so()
    {
        var session = await RunAsync();
        session.Status = SessionStatus.Failed;
        session.LastError = "the CLI fell over";
        await _store.UpsertAsync(session);

        var closed = await _sessions.CloseAsync(session.Id, "Finished the queue item on Merge requests.");

        Assert.Equal(SessionStatus.Failed, closed!.Status);
        Assert.True(closed.IsClosed);
    }

    [Fact]
    public async Task Closing_twice_is_harmless_and_keeps_the_first_reason()
    {
        var session = await RunAsync();

        await _sessions.CloseAsync(session.Id, "the first reason");
        var again = await _sessions.CloseAsync(session.Id, "the second reason");

        Assert.Equal("the first reason", again!.ClosedReason);
    }

    [Fact]
    public async Task Closing_a_session_that_has_gone_returns_nothing()
    {
        Assert.Null(await _sessions.CloseAsync("no-such-session"));
    }

    [Fact]
    public async Task Forcing_a_status_settles_the_session_and_is_stored()
    {
        var session = await RunAsync();

        var failed = await _sessions.SetStatusAsync(session.Id, SessionStatus.Failed);

        Assert.Equal(SessionStatus.Failed, failed!.Status);
        Assert.NotNull(failed.EndedAt);
        Assert.Equal(SessionStatus.Failed, (await _store.GetAsync(session.Id))!.Status);
    }

    [Fact]
    public async Task Forcing_a_session_back_to_pending_clears_the_end_time()
    {
        var session = await RunAsync();
        Assert.NotNull(session.EndedAt);

        var pending = await _sessions.SetStatusAsync(session.Id, SessionStatus.Pending);

        Assert.Equal(SessionStatus.Pending, pending!.Status);
        Assert.Null(pending.EndedAt);
    }

    [Fact]
    public async Task Forcing_a_status_on_a_session_that_has_gone_returns_nothing()
    {
        Assert.Null(await _sessions.SetStatusAsync("no-such-session", SessionStatus.Cancelled));
    }

    private sealed class StubRegistry : IProviderCliRegistry
    {
        public IReadOnlyList<IProviderCli> All => [];

        public IProviderCli Get(AiProvider provider) => new StubCli(provider);

        public Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default) =>
            Task.FromResult<IProviderCli>(new StubCli(provider));

        public Task<IReadOnlyList<IProviderCli>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IProviderCli>>([]);
    }

    /// <summary>Answers every turn immediately, so a run finishes without a real CLI.</summary>
    private sealed class StubCli(AiProvider provider) : IProviderCli
    {
        public AiProvider Provider => provider;
        public string DisplayName => provider.ToString();
        public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
            TurnRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return AgentEvent.Text_("done");
        }

        public Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default) =>
            Task.FromResult(ProviderAuthStatus.Unknown(provider));

        public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
            ("noop", []);

        public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() => ("noop", []);
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

        public Task<string> ComposeSystemPromptAsync(Agent agent, string? projectId, string? sessionId = null, CancellationToken ct = default) =>
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
        for (var attempt = 0; attempt < 10 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }

        GC.SuppressFinalize(this);
    }
}
