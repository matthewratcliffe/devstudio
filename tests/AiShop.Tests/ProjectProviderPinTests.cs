using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Application.Sessions;
using AiShop.Domain.Agents;
using AiShop.Domain.Common;
using AiShop.Domain.Projects;
using AiShop.Domain.Providers;
using AiShop.Domain.Sessions;
using AiShop.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiShop.Tests;

/// <summary>
/// A project pins one AI provider, and everything inside it runs on that provider whatever the
/// individual agent says.
/// </summary>
public class ProjectProviderPinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aishop-pin-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<Agent> _agents;
    private readonly JsonEntityStore<Project> _projects;
    private readonly RecordingRegistry _registry = new();
    private readonly SessionManager _sessions;

    public ProjectProviderPinTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root, MaxConcurrentSessions = 1 });

        _agents = Store<Agent>(options);
        _projects = Store<Project>(options);

        _sessions = new SessionManager(
            Store<ChatSession>(options),
            _agents,
            _registry,
            new StubWorkspace(_root),
            new StubAccounts(_root),
            _projects,
            options,
            NullLogger<SessionManager>.Instance);
    }

    private static JsonEntityStore<T> Store<T>(IOptions<OrchestratorOptions> options) where T : class, IEntity =>
        new(options, NullLogger<JsonEntityStore<T>>.Instance);

    [Fact]
    public async Task A_projects_provider_overrides_the_agents_own()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Claude agent", Provider = AiProvider.Claude });
        var project = await _projects.UpsertAsync(new Project { Name = "Codex shop", Provider = AiProvider.Codex });

        var session = await _sessions.StartAsync(new StartSessionRequest
        {
            AgentId = agent.Id,
            Prompt = "hello",
            ProjectId = project.Id,
        });

        await WaitForTurnAsync();

        Assert.Equal(AiProvider.Codex, session.Provider);
        Assert.Equal(AiProvider.Codex, _registry.LastResolved);
    }

    [Fact]
    public async Task A_project_pinned_to_a_custom_cli_passes_its_id_through()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Claude agent", Provider = AiProvider.Claude });
        var project = await _projects.UpsertAsync(new Project
        {
            Name = "Copilot shop",
            Provider = AiProvider.Custom,
            CliProviderId = "cli-1",
        });

        await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "hello", ProjectId = project.Id });
        await WaitForTurnAsync();

        Assert.Equal(AiProvider.Custom, _registry.LastResolved);
        Assert.Equal("cli-1", _registry.LastCliProviderId);
    }

    [Fact]
    public async Task Without_a_pin_the_agents_own_provider_is_used()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Claude agent", Provider = AiProvider.Claude });
        var project = await _projects.UpsertAsync(new Project { Name = "Anything goes" });

        var session = await _sessions.StartAsync(new StartSessionRequest
        {
            AgentId = agent.Id,
            Prompt = "hello",
            ProjectId = project.Id,
        });

        await WaitForTurnAsync();

        Assert.Equal(AiProvider.Claude, session.Provider);
        Assert.Equal(AiProvider.Claude, _registry.LastResolved);
    }

    [Fact]
    public async Task The_stored_agent_is_left_untouched_by_a_pin()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Claude agent", Provider = AiProvider.Claude });
        var project = await _projects.UpsertAsync(new Project { Name = "Codex shop", Provider = AiProvider.Codex });

        await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "hello", ProjectId = project.Id });
        await WaitForTurnAsync();

        var stored = await _agents.GetAsync(agent.Id);
        Assert.Equal(AiProvider.Claude, stored!.Provider);
    }

    private static Task WaitForTurnAsync() => Task.Delay(500);

    private sealed class RecordingRegistry : IProviderCliRegistry
    {
        public AiProvider? LastResolved { get; private set; }
        public string? LastCliProviderId { get; private set; }

        public IReadOnlyList<IProviderCli> All => [];

        public IProviderCli Get(AiProvider provider) => new SilentCli(provider);

        public Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default)
        {
            LastResolved = provider;
            LastCliProviderId = cliProviderId;
            return Task.FromResult<IProviderCli>(new SilentCli(provider));
        }

        public Task<IReadOnlyList<IProviderCli>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IProviderCli>>([]);
    }

    /// <summary>Answers immediately so the turn completes without touching a real CLI.</summary>
    private sealed class SilentCli(AiProvider provider) : IProviderCli
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
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
