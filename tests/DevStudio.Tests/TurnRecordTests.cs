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
/// What a turn leaves behind in the transcript: which model wrote each line, what it cost in
/// tokens, what it did to the files, and that the turn was counted at all. A conversation that
/// changes model half way through is unreadable unless each of those is recorded where it happened.
/// </summary>
public class TurnRecordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devstudio-turns-" + Guid.NewGuid().ToString("n"));
    private readonly JsonEntityStore<Agent> _agents;
    private readonly StubRegistry _registry = new();
    private readonly SessionManager _sessions;

    public TurnRecordTests()
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

    /// <summary>Hands over from one model to another after a single turn.</summary>
    private Task<Agent> HandoverAgent() => _agents.UpsertAsync(new Agent
    {
        Name = "Handover",
        Provider = AiProvider.Claude,
        Model = "sonnet",
        Effort = "low",
        OpeningModel = "fable",
        OpeningEffort = "high",
        OpeningTurns = 1,
    });

    [Fact]
    public async Task Every_message_either_way_is_a_turn()
    {
        var agent = await HandoverAgent();
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });

        // The stub answers, calls a tool, then answers again: two messages back, and the tool call
        // is not one of them.
        await WaitForTurnsAsync(session, 3);
        await _sessions.SendAsync(session.Id, "two");
        await WaitForTurnsAsync(session, 6);

        // The count the UI reads comes back off the store, so it has to survive being saved.
        var stored = await _sessions.GetAsync(session.Id);
        Assert.Equal(6, stored!.TurnCount);
    }

    [Fact]
    public async Task An_answer_that_never_arrived_is_not_counted()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Silent", Model = "sonnet" });

        _registry.Silent = true;
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });

        await WaitForStatusAsync(session);

        // The CLI said nothing, so only the message that was actually sent counts.
        Assert.Equal(1, session.TurnCount);
    }

    [Fact]
    public async Task A_handover_is_announced_in_the_transcript()
    {
        var agent = await HandoverAgent();
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });

        await WaitForTurnsAsync(session, 3);
        Assert.DoesNotContain(session.Messages, m => m.Role == MessageRole.System && m.Content.StartsWith("Model has changed"));

        await _sessions.SendAsync(session.Id, "two");
        await WaitForTurnsAsync(session, 6);

        Assert.Contains(session.Messages, m =>
            m.Role == MessageRole.System &&
            m.Content == "Model has changed from fable (high) to sonnet (low).");
    }

    /// <summary>
    /// The turn count is one way onto the cheaper model; the agent saying so is the other. The
    /// answer carrying the marker was written by the model that was already running, so the change
    /// lands on the turn after it.
    /// </summary>
    [Fact]
    public async Task An_agent_can_ask_for_the_handover_before_the_turn_count_reaches_it()
    {
        var agent = await _agents.UpsertAsync(new Agent
        {
            Name = "Handover",
            Model = "sonnet",
            Effort = "low",
            OpeningModel = "fable",
            OpeningEffort = "high",
            // Far enough out that nothing but the marker could end the opening.
            OpeningTurns = 99,
        });

        _registry.Answer = "The plan is settled. [CHANGE MODEL]";
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });
        await WaitForTurnsAsync(session, 3);

        Assert.True(session.HandoverRequested);
        Assert.Contains(session.Messages, m =>
            m.Role == MessageRole.System && m.Content.StartsWith("The agent asked to change model"));

        // The turn that asked still ran on the opening model, and the next one does not.
        Assert.Equal("fable", session.Messages.First(m => m.Role == MessageRole.Agent).Model);

        await _sessions.SendAsync(session.Id, "two");
        await WaitForTurnsAsync(session, 6);

        Assert.Equal("sonnet", session.ModelInUse);
        Assert.Contains(session.Messages, m =>
            m.Role == MessageRole.System && m.Content == "Model has changed from fable (high) to sonnet (low).");
    }

    /// <summary>
    /// With no handover configured there is still somewhere to go: the next model down the list
    /// configured for the CLI, which is written strongest first.
    /// </summary>
    [Fact]
    public async Task Asking_with_no_handover_configured_drops_to_the_next_model_down()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Solo", Provider = AiProvider.Claude, Model = "opus" });

        _registry.Answer = "[change model]";
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });
        await WaitForTurnsAsync(session, 3);

        Assert.Equal("sonnet", session.Model);

        await _sessions.SendAsync(session.Id, "two");
        await WaitForTurnsAsync(session, 6);

        Assert.Equal("sonnet", session.ModelInUse);
    }

    /// <summary>Nothing to drop to, so the marker leaves the conversation where it is.</summary>
    [Fact]
    public async Task Asking_on_the_cheapest_model_changes_nothing()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Cheapest", Provider = AiProvider.Claude, Model = "fable" });

        _registry.Answer = "[CHANGE MODEL]";
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });
        await WaitForTurnsAsync(session, 3);

        Assert.False(session.HandoverRequested);

        // Nothing written onto the conversation, so it keeps running on the agent's own model.
        Assert.Null(session.Model);
        Assert.Equal("fable", session.ModelInUse);
    }

    /// <summary>
    /// Counted as they happen, because a compaction empties the transcript they would otherwise be
    /// counted from.
    /// </summary>
    [Fact]
    public async Task Tool_calls_are_counted_across_the_conversation()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Busy", Model = "sonnet" });
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });

        await WaitForTurnsAsync(session, 3);
        Assert.Equal(1, session.ToolCallCount);

        await _sessions.SendAsync(session.Id, "two");
        await WaitForTurnsAsync(session, 6);

        var stored = await _sessions.GetAsync(session.Id);
        Assert.Equal(2, stored!.ToolCallCount);
    }

    [Fact]
    public async Task Staying_on_one_model_says_nothing()
    {
        var agent = await _agents.UpsertAsync(new Agent { Name = "Steady", Model = "sonnet", Effort = "low" });
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });

        await WaitForTurnsAsync(session, 3);
        await _sessions.SendAsync(session.Id, "two");
        await WaitForTurnsAsync(session, 6);

        Assert.DoesNotContain(session.Messages, m => m.Content.StartsWith("Model has changed"));
    }

    [Fact]
    public async Task Each_line_records_what_wrote_it()
    {
        var agent = await HandoverAgent();
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });

        await WaitForTurnsAsync(session, 3);
        await _sessions.SendAsync(session.Id, "two");
        await WaitForTurnsAsync(session, 6);

        var answers = session.Messages.Where(m => m.Role == MessageRole.Agent).ToList();

        Assert.Equal(("fable", "high"), (answers[0].Model, answers[0].Effort));
        Assert.Equal(("sonnet", "low"), (answers[^1].Model, answers[^1].Effort));
        Assert.Equal("sonnet", session.ModelInUse);
        Assert.Equal("low", session.EffortInUse);
    }

    [Fact]
    public async Task Tokens_are_counted_onto_the_line_and_the_session()
    {
        var agent = await HandoverAgent();
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });

        await WaitForTurnsAsync(session, 3);

        // A tool call closes one bubble and opens another, so a turn's usage lands on whichever
        // part of the answer was being written when the CLI reported it.
        var answers = session.Messages.Where(m => m.Role == MessageRole.Agent).ToList();

        Assert.Equal(2, answers.Count);
        Assert.All(answers, part => Assert.Equal(140, part.TotalTokens));
        Assert.Equal(100, answers[0].InputTokens);
        Assert.Equal(10, answers[0].OutputTokens);
        Assert.Equal(30, answers[0].CacheTokens);

        // The session counts the whole turn however the transcript was broken up.
        Assert.Equal(280, session.TotalTokens);

        await _sessions.SendAsync(session.Id, "two");
        await WaitForTurnsAsync(session, 6);

        Assert.Equal(560, session.TotalTokens);
    }

    [Fact]
    public async Task A_file_a_tool_changed_is_shown_as_a_diff()
    {
        var agent = await HandoverAgent();
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });

        await WaitForTurnsAsync(session, 3);

        var edit = Assert.Single(session.Messages, m => m.FilePath is not null);

        Assert.Equal("src/App.cs", edit.FilePath);
        Assert.Equal(1, edit.LinesAdded);
        Assert.Equal(1, edit.LinesRemoved);
        Assert.Equal("- old line\n+ new line", edit.Diff);
    }

    [Fact]
    public async Task Prose_written_after_a_tool_call_lands_below_it()
    {
        var agent = await HandoverAgent();
        var session = await _sessions.StartAsync(new StartSessionRequest { AgentId = agent.Id, Prompt = "one" });

        await WaitForTurnsAsync(session, 3);

        var roles = session.Messages.Select(m => m.Role).ToList();
        var content = session.Messages.Where(m => m.Role == MessageRole.Agent).Select(m => m.Content).ToList();

        Assert.Equal([MessageRole.User, MessageRole.Agent, MessageRole.Tool, MessageRole.Agent], roles);
        Assert.Equal(["working", "done"], content);
    }

    private static async Task WaitForTurnsAsync(ChatSession session, int turns)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (session.TurnCount < turns && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(turns, session.TurnCount);
    }

    private static async Task WaitForStatusAsync(ChatSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (session.IsLive && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    /// <summary>Reports a file change and its token usage, then answers, without a CLI anywhere.</summary>
    private sealed class ReportingCli : IProviderCli
    {
        public bool Silent { get; init; }

        /// <summary>What the second answer of the turn says, for testing markers in an answer.</summary>
        public string Answer { get; init; } = "done";

        public AiProvider Provider => AiProvider.Claude;
        public string DisplayName => "stub";
        public IReadOnlyList<LoginMethod> SupportedLoginMethods => [];

        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(
            TurnRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();

            if (Silent)
                yield break;

            yield return new AgentEvent(AgentEventKind.Usage, string.Empty) { Usage = new TokenUsage(100, 10, 20, 10) };
            yield return AgentEvent.Text_("working");
            yield return new AgentEvent(AgentEventKind.Tool, "src/App.cs")
            {
                ToolName = "Edit",
                ToolCallId = "call-1",
                Edit = new FileEdit("src/App.cs", "old line", "new line"),
            };
            yield return new AgentEvent(AgentEventKind.Usage, string.Empty) { Usage = new TokenUsage(100, 10, 20, 10) };
            yield return AgentEvent.Text_(Answer);
        }

        public Task<ProviderAuthStatus> GetAuthStatusAsync(string? homePath = null, CancellationToken ct = default) =>
            Task.FromResult(ProviderAuthStatus.Unknown(Provider));

        public (string FileName, IReadOnlyList<string> Arguments) BuildLoginCommand(LoginMethod method = LoginMethod.Browser) =>
            ("noop", []);

        public (string FileName, IReadOnlyList<string> Arguments) BuildLogoutCommand() => ("noop", []);
    }

    private sealed class StubRegistry : IProviderCliRegistry
    {
        /// <summary>Hands back a CLI that answers with nothing at all.</summary>
        public bool Silent { get; set; }

        /// <summary>What every answer says, so a turn can carry a marker.</summary>
        public string Answer { get; set; } = "done";

        public IReadOnlyList<IProviderCli> All => [];
        public IProviderCli Get(AiProvider provider) => new ReportingCli { Silent = Silent, Answer = Answer };

        public Task<IProviderCli> ResolveAsync(AiProvider provider, string? cliProviderId, CancellationToken ct = default) =>
            Task.FromResult<IProviderCli>(new ReportingCli { Silent = Silent, Answer = Answer });

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
