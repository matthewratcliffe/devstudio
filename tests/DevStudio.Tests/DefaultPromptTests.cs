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
/// An agent can carry its own opening prompt, which is what a session started with nothing typed
/// in — a schedule, or one click from the Agents page — sends as its first turn.
/// </summary>
public class DefaultPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-firstprompt-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<Agent> _agents;
    private readonly RecordingRegistry _registry = new();
    private readonly SessionManager _sessions;

    public DefaultPromptTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root, MaxConcurrentSessions = 1 });
        _agents = Store<Agent>(options);

        _sessions = new SessionManager(
            Store<ChatSession>(options),
            _agents,
            _registry,
            new StubWorkspace(_root),
            new StubAccounts(_root),
            Store<Project>(options),
            options,
            NullLogger<SessionManager>.Instance);
    }

    private static JsonEntityStore<T> Store<T>(IOptions<OrchestratorOptions> options) where T : class, IEntity =>
        new(options, NullLogger<JsonEntityStore<T>>.Instance);

    [Fact]
    public async Task A_session_started_with_no_prompt_sends_the_agents_default()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Triage", DefaultPrompt = "Triage the overnight failures." });

        await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = string.Empty });

        Assert.Equal("Triage the overnight failures.", await _registry.WaitForPromptAsync());
    }

    [Fact]
    public async Task A_prompt_that_was_typed_in_wins_over_the_default()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Triage", DefaultPrompt = "Triage the overnight failures." });

        await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "Look at the deploy instead." });

        Assert.Equal("Look at the deploy instead.", await _registry.WaitForPromptAsync());
    }

    [Fact]
    public async Task The_session_is_titled_from_the_default_when_nothing_was_typed()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Triage", DefaultPrompt = "Triage the overnight failures." });

        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "   " });

        await _registry.WaitForPromptAsync();
        Assert.Contains("Triage the overnight", session.Title);
    }

    private sealed class RecordingRegistry : IProviderCliRegistry
    {
        private readonly TaskCompletionSource<string> _prompt =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<IProviderCli> All => [];

        public async Task<string> WaitForPromptAsync()
        {
            var finished = await Task.WhenAny(_prompt.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(_prompt.Task, finished);
            return await _prompt.Task;
        }

        public IProviderCli Get(AiProvider provider) => new RecordingCli(provider, _prompt);

        public Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default) =>
            Task.FromResult<IProviderCli>(new RecordingCli(provider, _prompt));

        public Task<IReadOnlyList<IProviderCli>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IProviderCli>>([]);
    }

    /// <summary>Records the prompt of the first turn, then answers so the session finishes.</summary>
    private sealed class RecordingCli(AiProvider provider, TaskCompletionSource<string> prompt) : IProviderCli
    {
        public AiProvider Provider => provider;
        public string DisplayName => provider.ToString();
        public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
            TurnRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            prompt.TrySetResult(request.Prompt);
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
        // The turn is still finishing when the assertion passes, and it writes the session file
        // into this folder as it goes, so the first delete can lose a race with it.
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
