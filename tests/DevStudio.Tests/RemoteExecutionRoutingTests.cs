using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Remoting;
using DevStudio.Application.Sessions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Remoting;
using DevStudio.Domain.Sessions;
using DevStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

/// <summary>
/// Which machine a session's work goes to, and — just as importantly — that everything belonging to
/// one turn goes to the *same* machine. A workspace prepared here and a CLI run over there would
/// hand the CLI a path that names nothing.
/// </summary>
public sealed class RemoteExecutionRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-routing-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<Agent> _agents;
    private readonly JsonEntityStore<ChatSession> _sessions;
    private readonly SessionManager _manager;
    private readonly RecordingHost _local = new(null, "This machine");
    private readonly RecordingHost _remote = new("desk", "Desk machine");

    public RemoteExecutionRoutingTests()
    {
        var options = Options.Create(new OrchestratorOptions
        {
            DataPath = _root,
            ScratchPath = Path.Combine(_root, "scratch"),
        });

        _agents = new JsonEntityStore<Agent>(options, NullLogger<JsonEntityStore<Agent>>.Instance);
        _sessions = new JsonEntityStore<ChatSession>(options, NullLogger<JsonEntityStore<ChatSession>>.Instance);

        _manager = new SessionManager(
            _sessions,
            _agents,
            new TwoHostResolver(_local, _remote),
            new JsonEntityStore<Project>(options, NullLogger<JsonEntityStore<Project>>.Instance),
            new JsonEntityStore<GlobalSettings>(options, NullLogger<JsonEntityStore<GlobalSettings>>.Instance),
            options,
            NullLogger<SessionManager>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<Agent> AgentAsync(string? remoteInstanceId = null) =>
        await _agents.UpsertAsync(new Agent
        {
            Name = "worker",
            Provider = AiProvider.Claude,
            RemoteInstanceId = remoteInstanceId,
        });

    [Fact]
    public async Task A_session_runs_here_by_default()
    {
        var agent = await AgentAsync();

        var session = await _manager.RunToCompletionAsync(
            new StartSessionRequest { AgentId = agent.Id, Prompt = "hello" },
            TimeSpan.FromSeconds(10));

        Assert.Null(session.RemoteInstanceId);
        Assert.True(_local.PreparedWorkspace);
        Assert.False(_remote.PreparedWorkspace);
    }

    [Fact]
    public async Task An_agent_pointed_at_an_instance_runs_its_turns_there()
    {
        var agent = await AgentAsync("desk");

        var session = await _manager.RunToCompletionAsync(
            new StartSessionRequest { AgentId = agent.Id, Prompt = "hello" },
            TimeSpan.FromSeconds(10));

        Assert.Equal("desk", session.RemoteInstanceId);
        Assert.Equal("Desk machine", session.RemoteInstanceName);
        Assert.True(_remote.RanTurn);
        Assert.False(_local.RanTurn);
    }

    /// <summary>
    /// Everything a turn needs is filesystem- or login-bound, so it all has to come from one place.
    /// </summary>
    [Fact]
    public async Task The_workspace_the_login_and_the_cli_all_come_from_the_same_machine()
    {
        var agent = await AgentAsync("desk");

        var session = await _manager.RunToCompletionAsync(
            new StartSessionRequest { AgentId = agent.Id, Prompt = "hello" },
            TimeSpan.FromSeconds(10));

        Assert.True(_remote.PreparedWorkspace);
        Assert.True(_remote.ResolvedAccount);
        Assert.True(_remote.RanTurn);

        Assert.False(_local.PreparedWorkspace);
        Assert.False(_local.ResolvedAccount);
        Assert.False(_local.RanTurn);

        // The path recorded is the far side's, handed straight back to it and never opened here.
        Assert.Equal(RecordingHost.RemoteWorkspacePath, session.WorkingDirectory);
    }

    /// <summary>
    /// A one-off override, so an agent can be sent elsewhere for a single run without being edited —
    /// which is what the picker on the new-session page does.
    /// </summary>
    [Fact]
    public async Task The_request_can_override_where_the_agent_normally_runs()
    {
        var agent = await AgentAsync();

        var session = await _manager.RunToCompletionAsync(
            new StartSessionRequest
            {
                AgentId = agent.Id,
                Prompt = "hello",
                RemoteInstanceId = "desk",
            },
            TimeSpan.FromSeconds(10));

        Assert.Equal("desk", session.RemoteInstanceId);
        Assert.True(_remote.RanTurn);
    }

    [Fact]
    public async Task A_session_pointed_at_an_instance_that_is_gone_fails_rather_than_running_here()
    {
        var agent = await AgentAsync("vanished");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "hello" }));

        Assert.Contains("vanished", ex.Message);
        Assert.False(_local.RanTurn);
    }

    /// <summary>One host stands in for this machine, one for a paired instance.</summary>
    private sealed class TwoHostResolver(RecordingHost local, RecordingHost remote) : IExecutionHostResolver
    {
        public IExecutionHost Local => local;

        public Task<IExecutionHost> ResolveAsync(string? remoteInstanceId, CancellationToken ct = default) =>
            remoteInstanceId switch
            {
                null => Task.FromResult<IExecutionHost>(local),
                "desk" => Task.FromResult<IExecutionHost>(remote),
                _ => throw new InvalidOperationException($"'{remoteInstanceId}' has not been paired."),
            };

        public Task<IReadOnlyList<RemoteInstance>> AvailableAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteInstance>>([new RemoteInstance { Id = "desk", Name = "Desk machine" }]);
    }

    /// <summary>An execution host that records which of its services were actually used.</summary>
    private sealed class RecordingHost(string? id, string name) : IExecutionHost
    {
        public const string RemoteWorkspacePath = "/remote/workspace";

        public string? RemoteInstanceId => id;
        public string DisplayName => name;

        public bool PreparedWorkspace { get; private set; }
        public bool ResolvedAccount { get; private set; }
        public bool RanTurn { get; private set; }

        public IProviderCliRegistry Clis => new Registry(() => RanTurn = true);
        public IWorkspaceService Workspaces => new Workspace(() => PreparedWorkspace = true);
        public IAccountService Accounts => new AccountStub(() => ResolvedAccount = true);

        public IWorkspaceFileService Files => throw new NotSupportedException();
        public ITerminalService Terminals => throw new NotSupportedException();

        public Task<RemoteHostConfig> GetConfigAsync(CancellationToken ct = default) =>
            Task.FromResult(RemoteHostConfig.Empty(name));

        public Task<IReadOnlyList<string>> GetBranchesAsync(string repositoryId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        private sealed class Registry(Action onRun) : IProviderCliRegistry
        {
            public IReadOnlyList<IProviderCli> All => [new Cli(onRun)];
            public IProviderCli Get(AiProvider provider) => new Cli(onRun);

            public Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default) =>
                Task.FromResult<IProviderCli>(new Cli(onRun));

            public Task<IReadOnlyList<IProviderCli>> GetAllAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<IProviderCli>>([new Cli(onRun)]);
        }

        private sealed class Cli(Action onRun) : IProviderCli
        {
            public AiProvider Provider => AiProvider.Claude;
            public string DisplayName => "recording";
            public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

            public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
                TurnRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                onRun();
                yield return AgentEvent.Text_("done");
                await Task.CompletedTask;
            }

            public Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default) =>
                Task.FromResult(ProviderAuthStatus.Unknown(AiProvider.Claude));

            public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
                ("noop", []);

            public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() => ("noop", []);
        }

        private sealed class Workspace(Action onPrepare) : IWorkspaceService
        {
            public Task<WorkspacePlan> PlanAsync(
                Agent agent,
                string sessionId,
                string? projectId,
                IReadOnlyList<string>? extraServerIds,
                CancellationToken ct = default) =>
                Task.FromResult(new WorkspacePlan { Agent = agent, SessionId = sessionId, ProjectId = projectId });

            public Task<SessionWorkspace> PrepareAsync(WorkspacePlan plan, CancellationToken ct = default)
            {
                onPrepare();
                return Task.FromResult(new SessionWorkspace(RemoteWorkspacePath, null, null, plan.ProjectId));
            }

            public Task<SessionWorkspace> PrepareAsync(Agent agent, string sessionId, string? projectId, CancellationToken ct = default) =>
                PrepareAsync(agent, sessionId, projectId, null, ct);

            public async Task<SessionWorkspace> PrepareAsync(
                Agent agent,
                string sessionId,
                string? projectId,
                IReadOnlyList<string>? extraServerIds,
                CancellationToken ct = default) =>
                await PrepareAsync(await PlanAsync(agent, sessionId, projectId, extraServerIds, ct), ct);

            public Task ReleaseAsync(SessionWorkspace workspace, CancellationToken ct = default) => Task.CompletedTask;
            public Task MaterialiseSkillsAsync(Agent agent, string workspacePath, CancellationToken ct = default) => Task.CompletedTask;

            public Task<IReadOnlyList<string>> MaterialiseMcpAsync(
                Agent agent,
                string workspacePath,
                IReadOnlyList<string>? extraServerIds = null,
                CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

            public Task MaterialiseProjectFilesAsync(string projectId, string workspacePath, CancellationToken ct = default) => Task.CompletedTask;
            public Task MaterialiseGlobalFilesAsync(string workspacePath, CancellationToken ct = default) => Task.CompletedTask;

            public Task<string> ComposeSystemPromptAsync(
                Agent agent,
                string? projectId,
                string? sessionId = null,
                TokenTactics tactics = TokenTactics.None,
                string? handoverModel = null,
                CancellationToken ct = default) => Task.FromResult(string.Empty);

            public Task WriteGuidanceAsync(string workspacePath, IEnumerable<GuidanceMessage> guidance, CancellationToken ct = default) =>
                Task.CompletedTask;
        }

        private sealed class AccountStub(Action onResolve) : IAccountService
        {
            public Task<ResolvedAccount> ResolveAsync(Agent agent, string? projectId, CancellationToken ct = default)
            {
                onResolve();
                return Task.FromResult(new ResolvedAccount(null, "default", "/remote/home"));
            }

            public Task<string> GetHomePathAsync(string accountId, CancellationToken ct = default) =>
                throw new NotSupportedException();

            public Task<ProviderAccount> CreateAsync(string name, AiProvider provider, CancellationToken ct = default) =>
                throw new NotSupportedException();

            public Task<ProviderAccount> CreateAsync(string name, AiProvider provider, string? cliProviderId, CancellationToken ct = default) =>
                throw new NotSupportedException();

            public Task<bool> DeleteAsync(string accountId, bool deleteCredentials, CancellationToken ct = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<ProviderAccount>> RefreshAuthStateAsync(CancellationToken ct = default) =>
                throw new NotSupportedException();
        }
    }
}
