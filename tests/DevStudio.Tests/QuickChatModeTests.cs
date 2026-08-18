using DevStudio.Application.Abstractions;
using DevStudio.Application.Agents;
using DevStudio.Application.Common;
using DevStudio.Application.Sessions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;
using DevStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class QuickChatModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-quick-" + Guid.NewGuid().ToString("n"));
    private readonly RecordingSessions _sessions = new();
    private readonly QuickChatService _service;
    private readonly IEntityStore<Agent> _agents;

    public QuickChatModeTests()
    {
        _agents = new JsonEntityStore<Agent>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<Agent>>.Instance);

        var sessions = new JsonEntityStore<ChatSession>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<ChatSession>>.Instance);

        _service = new QuickChatService(_agents, new StubRegistry(), _sessions, sessions);
    }

    [Fact]
    public async Task The_mode_the_user_picked_is_what_the_session_starts_in()
    {
        await _service.StartAsync(AiProvider.Claude, null, "hello", null, PermissionMode.AcceptEdits);

        Assert.Equal(PermissionMode.AcceptEdits, _sessions.Last!.PermissionMode);
    }

    [Fact]
    public async Task An_explicit_choice_wins_over_the_mcp_rule()
    {
        await _service.StartAsync(AiProvider.Claude, null, "hello", ["server-1"], PermissionMode.Unrestricted);

        Assert.Equal(PermissionMode.Unrestricted, _sessions.Last!.PermissionMode);
    }

    [Fact]
    public async Task Without_a_choice_attaching_servers_still_lifts_the_session_out_of_plan_mode()
    {
        await _service.StartAsync(AiProvider.Claude, null, "hello", ["server-1"]);

        Assert.Equal(PermissionMode.AcceptEdits, _sessions.Last!.PermissionMode);
    }

    [Fact]
    public async Task Without_a_choice_and_without_servers_the_agent_default_is_left_alone()
    {
        await _service.StartAsync(AiProvider.Claude, null, "hello");

        Assert.Null(_sessions.Last!.PermissionMode);
    }

    [Fact]
    public async Task The_model_schedule_chosen_in_the_chat_reaches_the_session()
    {
        await _service.StartAsync(
            AiProvider.Claude, null, "hello", null, null,
            new SessionModelSettings("sonnet", "low", "fable", "high", 2));

        var model = _sessions.Last!.Model;

        Assert.Equal("fable", model!.OpeningModel);
        Assert.Equal(2, model.OpeningTurns);
    }

    [Fact]
    public async Task Changing_cli_mid_chat_drops_the_resume_id_the_old_one_owned()
    {
        var started = await _service.StartAsync(AiProvider.Claude, null, "hello");
        started.ProviderSessionId = "claude-side-id";
        _sessions.Store(started);

        var switched = await _service.SwitchProviderAsync(started.Id, AiProvider.Codex, null);

        Assert.Equal(AiProvider.Codex, switched.Provider);
        Assert.Null(switched.ProviderSessionId);
    }

    [Fact]
    public async Task A_configured_agent_keeps_the_cli_it_was_built_around()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Reviewer", Provider = AiProvider.Codex });
        var session = new ChatSession { Id = "session-1", AgentId = agent.Id, Provider = AiProvider.Codex };
        _sessions.Store(session);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.SwitchProviderAsync(session.Id, AiProvider.Claude, null));

        Assert.Contains("Reviewer", error.Message);
    }

    private sealed class RecordingSessions : ISessionManager
    {
        private readonly Dictionary<string, ChatSession> _stored = [];

        public StartSessionRequest? Last { get; private set; }

        /// <summary>Stands in for a session the manager is already holding.</summary>
        public void Store(ChatSession session) => _stored[session.Id] = session;

        public IReadOnlyList<ChatSession> Live => [];

        public event Action<ChatSession>? SessionUpdated;

        public Task<ChatSession> StartAsync(StartSessionRequest request, CancellationToken ct = default)
        {
            Last = request;
            var session = new ChatSession { AgentId = request.AgentId };
            Store(session);
            SessionUpdated?.Invoke(session);
            return Task.FromResult(session);
        }

        public Task SendAsync(string sessionId, string message, CancellationToken ct = default) => Task.CompletedTask;

        public Task<GuidanceMessage> SendGuidanceAsync(
            string sessionId,
            string guidance,
            string source = "operator",
            bool interrupt = false,
            CancellationToken ct = default) => Task.FromResult(new GuidanceMessage { Text = guidance, Source = source });

        public Task<IReadOnlyList<GuidanceMessage>> TakeGuidanceAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GuidanceMessage>>([]);

        public Task<ToolApproval?> ResolveApprovalAsync(string sessionId, string approvalId, bool allow, CancellationToken ct = default) =>
            Task.FromResult<ToolApproval?>(null);

        public Task CancelAsync(string sessionId) => Task.CompletedTask;

        public Task<ChatSession?> SetStatusAsync(
            string sessionId,
            SessionStatus status,
            CancellationToken ct = default) => Task.FromResult<ChatSession?>(null);

        public Task<ChatSession?> CloseAsync(string sessionId, string? reason = null, CancellationToken ct = default) =>
            Task.FromResult(_stored.TryGetValue(sessionId, out var session) ? session : null);

        public Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(_stored.TryGetValue(sessionId, out var session) ? session : null);

        public Task NotifyUpdatedAsync(ChatSession session, CancellationToken ct = default)
        {
            _stored[session.Id] = session;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ChatSession>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChatSession>>([]);

        public Task<bool> DeleteAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<ChatSession> RunToCompletionAsync(StartSessionRequest request, TimeSpan timeout, CancellationToken ct = default) =>
            StartAsync(request, ct);
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
            yield return AgentEvent.Text_("ok");
        }

        public Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default) =>
            Task.FromResult(ProviderAuthStatus.Unknown(provider));

        public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
            ("noop", []);

        public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() => ("noop", []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
