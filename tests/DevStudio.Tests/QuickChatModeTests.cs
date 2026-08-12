using DevStudio.Application.Abstractions;
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

    public QuickChatModeTests()
    {
        var agents = new JsonEntityStore<Agent>(
            Options.Create(new OrchestratorOptions { DataPath = _root }),
            NullLogger<JsonEntityStore<Agent>>.Instance);

        _service = new QuickChatService(agents, new StubRegistry(), _sessions);
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

    private sealed class RecordingSessions : ISessionManager
    {
        public StartSessionRequest? Last { get; private set; }

        public IReadOnlyList<ChatSession> Live => [];

        public event Action<ChatSession>? SessionUpdated;

        public Task<ChatSession> StartAsync(StartSessionRequest request, CancellationToken ct = default)
        {
            Last = request;
            var session = new ChatSession { AgentId = request.AgentId };
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

        public Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<ChatSession?>(null);

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
