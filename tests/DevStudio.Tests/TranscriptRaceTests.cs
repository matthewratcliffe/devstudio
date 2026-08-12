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
/// A turn writes the transcript from a background pump while a browser circuit renders the same
/// session. Rendering enumerates the message list, so anything that mutates that list in place
/// throws "collection was modified" mid-render and takes the circuit down.
/// </summary>
public class TranscriptRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-race-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<Agent> _agents;
    private readonly SessionManager _sessions;

    public TranscriptRaceTests()
    {
        var options = Options.Create(new OrchestratorOptions { DataPath = _root, HomePath = _root, MaxConcurrentSessions = 1 });
        _agents = Store<Agent>(options);

        _sessions = new SessionManager(
            Store<ChatSession>(options),
            _agents,
            new ChattyRegistry(),
            new StubWorkspace(_root),
            new StubAccounts(_root),
            Store<Project>(options),
            options,
            NullLogger<SessionManager>.Instance);
    }

    private static JsonEntityStore<T> Store<T>(IOptions<OrchestratorOptions> options) where T : class, IEntity =>
        new(options, NullLogger<JsonEntityStore<T>>.Instance);

    [Fact]
    public async Task Rendering_the_transcript_while_a_turn_writes_it_does_not_throw()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Chatty" });
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "go" });

        // Stands in for the render loop: read every message, over and over, while the turn runs.
        Exception? caught = null;
        var rendering = true;
        var renderer = Task.Run(() =>
        {
            try
            {
                while (Volatile.Read(ref rendering))
                {
                    foreach (var message in session.Messages)
                        _ = message.Content.Length;

                    foreach (var guidance in session.PendingGuidance)
                        _ = guidance.Text.Length;
                }
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });

        while (session.Status is SessionStatus.Starting or SessionStatus.Running)
            await Task.Delay(10);

        Volatile.Write(ref rendering, false);
        await renderer;

        Assert.Null(caught);
        Assert.True(session.Messages.Count > 100, $"the turn only wrote {session.Messages.Count} messages");
    }

    [Fact]
    public async Task A_finished_tool_call_gets_its_duration()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Chatty" });
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "go" });

        while (session.Status is SessionStatus.Starting or SessionStatus.Running)
            await Task.Delay(10);

        // The result arrives long after the call and with prose in between, so the line it belongs
        // to is found by id rather than by position.
        var tools = session.Messages.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.NotEmpty(tools);
        Assert.All(tools, t => Assert.NotNull(t.DurationMs));
        Assert.All(tools, t => Assert.True(t.DurationMs >= 0));
    }

    /// <summary>Writes a great many transcript entries, which is what opens the window.</summary>
    private sealed class ChattyCli : IProviderCli
    {
        public AiProvider Provider => AiProvider.Claude;
        public string DisplayName => "Chatty";
        public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
            TurnRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < 400; i++)
            {
                // A tool event closes the open bubble and opens another, so each one appends twice.
                yield return new AgentEvent(AgentEventKind.Tool, $"step {i}")
                {
                    ToolName = "Bash",
                    ToolCallId = $"call-{i}",
                };
                yield return AgentEvent.Text_($"working on {i} ");
                yield return new AgentEvent(AgentEventKind.ToolCompleted, string.Empty) { ToolCallId = $"call-{i}" };
                await Task.Yield();
            }
        }

        public Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default) =>
            Task.FromResult(ProviderAuthStatus.Unknown(AiProvider.Claude));

        public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
            ("noop", []);

        public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() => ("noop", []);
    }

    private sealed class ChattyRegistry : IProviderCliRegistry
    {
        public IReadOnlyList<IProviderCli> All => [];
        public IProviderCli Get(AiProvider provider) => new ChattyCli();

        public Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default) =>
            Task.FromResult<IProviderCli>(new ChattyCli());

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
