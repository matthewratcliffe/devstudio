using System.Collections.Concurrent;
using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Agents;
using DevStudio.Application.Common;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Application.Sessions;

/// <summary>
/// Default <see cref="ISessionManager"/>. Each session owns a queue of turns pumped by a single
/// background task, so a conversation stays ordered while unrelated sessions run in parallel.
/// </summary>
public sealed class SessionManager : ISessionManager, IAsyncDisposable
{
    private sealed class LiveSession
    {
        public required ChatSession Session { get; init; }
        public required Channel<string> Turns { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required SessionWorkspace Workspace { get; init; }
        public required Agent Agent { get; init; }
        public Task Pump { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Idle { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int QueuedTurns;

        /// <summary>Set while a turn is in flight, so guidance can interrupt just that turn.</summary>
        public CancellationTokenSource? TurnCancellation { get; set; }

        /// <summary>Distinguishes a guidance interrupt from the user pressing stop.</summary>
        public bool InterruptedForGuidance { get; set; }
    }

    private readonly ConcurrentDictionary<string, LiveSession> _live = new();
    private readonly ConcurrentDictionary<string, byte> _deleting = new();
    private readonly IEntityStore<ChatSession> _sessions;
    private readonly IEntityStore<Agent> _agents;
    private readonly IProviderCliRegistry _clis;
    private readonly IWorkspaceService _workspaces;
    private readonly IAccountService _accounts;
    private readonly IEntityStore<Project> _projects;
    private readonly IEntityStore<GlobalSettings> _globalSettings;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SessionManager> _logger;
    private readonly SemaphoreSlim _concurrency;

    public SessionManager(
        IEntityStore<ChatSession> sessions,
        IEntityStore<Agent> agents,
        IProviderCliRegistry clis,
        IWorkspaceService workspaces,
        IAccountService accounts,
        IEntityStore<Project> projects,
        IEntityStore<GlobalSettings> globalSettings,
        IOptions<OrchestratorOptions> options,
        ILogger<SessionManager> logger)
    {
        _sessions = sessions;
        _agents = agents;
        _clis = clis;
        _workspaces = workspaces;
        _accounts = accounts;
        _projects = projects;
        _globalSettings = globalSettings;
        _options = options.Value;
        _logger = logger;
        _concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentSessions));
    }

    public event Action<ChatSession>? SessionUpdated;

    public IReadOnlyList<ChatSession> Live =>
        _live.Values.Select(l => l.Session).OrderByDescending(s => s.CreatedAt).ToList();

    public async Task<ChatSession> StartAsync(StartSessionRequest request, CancellationToken ct = default)
    {
        var agent = await _agents.GetAsync(request.AgentId, ct)
                    ?? throw new InvalidOperationException($"Agent '{request.AgentId}' no longer exists.");

        if (!agent.Enabled)
            throw new InvalidOperationException($"Agent '{agent.Name}' is disabled.");

        // A project pins one provider for everything inside it, so a session started from an agent
        // configured for another CLI still runs on the project's choice.
        var projectId = request.ProjectId ?? agent.ProjectId;
        agent = await ApplyProjectProviderAsync(agent, projectId, ct);

        // Nothing typed in falls back to the agent's own opening prompt, so a schedule or a
        // one-click start still has something to work from.
        var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? agent.DefaultPrompt : request.Prompt;

        var session = new ChatSession
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? BuildTitle(prompt) : request.Title!,
            AgentId = agent.Id,
            AgentName = agent.Name,
            ParentSessionId = request.ParentSessionId,
            Provider = agent.Provider,
            CliProviderId = agent.CliProviderId,
            PermissionMode = request.PermissionMode ?? agent.PermissionMode,
            TokenMinimisation = request.TokenMinimisation,
            UseLlmForPromptingTips = agent.UseLlmForPromptingTips ??
                                     (await _globalSettings.GetAsync(GlobalSettings.WellKnownId, ct))?.UseLlmForPromptingTips == true,
            Trigger = request.Trigger,
            ProjectId = request.ProjectId ?? agent.ProjectId,
            McpServerIds = [.. request.McpServerIds],
            WorkflowRunId = request.WorkflowRunId,
            ScheduleId = request.ScheduleId,
            QueueItemId = request.QueueItemId,
            Status = SessionStatus.Starting,
        };

        // Applied before the first turn is queued, or an opening model chosen in the chat would miss
        // the very turn it was chosen for.
        request.Model?.ApplyTo(session);

        SessionWorkspace workspace;
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectoryOverride))
        {
            // A workflow step reusing the previous step's directory still needs its own skills and MCP config.
            workspace = new SessionWorkspace(request.WorkingDirectoryOverride!, agent.RepositoryId, null, session.ProjectId);
            await _workspaces.MaterialiseSkillsAsync(agent, workspace.Path, ct);
            await _workspaces.MaterialiseMcpAsync(agent, workspace.Path, session.McpServerIds, ct);
        }
        else
        {
            workspace = await _workspaces.PrepareAsync(agent, session.Id, session.ProjectId, session.McpServerIds, ct);
        }

        var account = await _accounts.ResolveAsync(agent, session.ProjectId, ct);
        session.AccountId = account.AccountId;
        session.AccountName = account.Name;

        session.WorkingDirectory = workspace.Path;
        session.RepositoryId = workspace.RepositoryId;
        session.WorktreeId = workspace.Worktree?.Id;
        await _sessions.UpsertAsync(session, ct);

        var live = new LiveSession
        {
            Session = session,
            Turns = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true }),
            Cancellation = new CancellationTokenSource(),
            Workspace = workspace,
            Agent = agent,
        };
        _live[session.Id] = live;

        Interlocked.Increment(ref live.QueuedTurns);
        await live.Turns.Writer.WriteAsync(prompt, CancellationToken.None);
        live.Pump = Task.Run(() => PumpAsync(live), CancellationToken.None);

        Notify(session);
        return session;
    }

    public async Task SendAsync(string sessionId, string message, CancellationToken ct = default)
    {
        await EnsureOpenAsync(sessionId, ct);

        if (!_live.TryGetValue(sessionId, out var live))
        {
            live = await ReviveAsync(sessionId, ct)
                   ?? throw new InvalidOperationException("That session can no longer be resumed.");
        }

        Interlocked.Increment(ref live.QueuedTurns);
        if (live.Idle.Task.IsCompleted)
            live.Idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await live.Turns.Writer.WriteAsync(message, ct);
    }

    /// <summary>
    /// Returns the agent as the project wants it run. The stored agent is never modified — the
    /// override lives only for the duration of this session.
    /// </summary>
    private async Task<Agent> ApplyProjectProviderAsync(Agent agent, string? projectId, CancellationToken ct)
    {
        if (projectId is null)
            return agent;

        var project = await _projects.GetAsync(projectId, ct);
        if (project?.Provider is not { } provider)
            return agent;

        if (provider == agent.Provider && project.CliProviderId == agent.CliProviderId)
            return agent;

        return new Agent
        {
            Id = agent.Id,
            CreatedAt = agent.CreatedAt,
            Name = agent.Name,
            Description = agent.Description,
            Provider = provider,
            CliProviderId = project.CliProviderId,
            Model = agent.Model,
            Effort = agent.Effort,
            OpeningModel = agent.OpeningModel,
            OpeningEffort = agent.OpeningEffort,
            OpeningTurns = agent.OpeningTurns,
            SystemPrompt = agent.SystemPrompt,
            DefaultPrompt = agent.DefaultPrompt,
            PermissionMode = agent.PermissionMode,
            TokenMinimisation = agent.TokenMinimisation,
            PromptingTips = agent.PromptingTips,
            UseLlmForPromptingTips = agent.UseLlmForPromptingTips,
            ProjectId = agent.ProjectId,
            AccountId = agent.AccountId,
            FallbackAccountId = agent.FallbackAccountId,
            RepositoryId = agent.RepositoryId,
            BaseBranch = agent.BaseBranch,
            UseWorktree = agent.UseWorktree,
            SkillIds = agent.SkillIds,
            McpServerIds = agent.McpServerIds,
            Environment = agent.Environment,
            ExtraArguments = agent.ExtraArguments,
            Enabled = agent.Enabled,
            Accent = agent.Accent,
            IsQuickChat = agent.IsQuickChat,
        };
    }

    /// <summary>Prompt used when guidance arrives with no turn of its own to ride along with.</summary>
    private const string GuidanceContinuationPrompt =
        "New guidance has come in. Take it into account and carry on with the work.";

    /// <summary>
    /// Grants or refuses a parked permission request. Granting adds the rule to the session, so it
    /// applies from the next turn on and the operator is not asked the same question twice.
    /// </summary>
    public async Task<ToolApproval?> ResolveApprovalAsync(
        string sessionId,
        string approvalId,
        bool allow,
        CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        var approval = session?.Approvals.FirstOrDefault(a => a.Id == approvalId);
        if (session is null || approval is null)
            return null;

        approval.Status = allow ? ApprovalStatus.Allowed : ApprovalStatus.Denied;

        if (allow && approval.SuggestedRule is { Length: > 0 } rule && !session.AllowedTools.Contains(rule))
            session.AllowedTools = Published(session, session.AllowedTools, list => list.Add(rule));

        await _sessions.UpsertAsync(session, ct);
        Notify(session);

        return approval;
    }

    /// <summary>
    /// Parks a refused tool call for the operator. Identical requests collapse into one: a denied
    /// command is usually retried, and one row per attempt would bury the decision.
    /// </summary>
    private static void RecordApproval(ChatSession session, string? toolName, string detail)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;

        var trimmed = detail.Trim();
        if (session.Approvals.Any(a =>
                a.ToolName == toolName &&
                a.Detail == trimmed &&
                a.Status != ApprovalStatus.Denied))
        {
            return;
        }

        var approval = new ToolApproval
        {
            ToolName = toolName,
            Detail = trimmed,
            SuggestedRule = SuggestRule(toolName, trimmed),
        };

        session.Approvals = Published(session, session.Approvals, list => list.Add(approval));
    }

    /// <summary>
    /// Turns a refused call into a rule. For a shell command that means the leading words up to the
    /// first argument — approving "gh pr view 42" should also cover "gh pr view 43", but must not
    /// quietly hand over the whole of gh.
    /// </summary>
    public static string SuggestRule(string toolName, string detail)
    {
        if (!string.Equals(toolName, "Bash", StringComparison.OrdinalIgnoreCase) || detail.Length == 0)
            return toolName;

        var words = new List<string>();
        foreach (var word in detail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Stop at the first thing that is not a bare word: flags, paths, pipes, substitutions.
            if (!word.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') || word.StartsWith('-'))
                break;

            words.Add(word);
            if (words.Count == 3)
                break;
        }

        return words.Count == 0 ? toolName : $"{toolName}({string.Join(' ', words)}:*)";
    }

    public async Task<GuidanceMessage> SendGuidanceAsync(
        string sessionId,
        string guidance,
        string source = "operator",
        bool interrupt = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(guidance))
            throw new ArgumentException("Guidance cannot be empty.", nameof(guidance));

        await EnsureOpenAsync(sessionId, ct);

        if (!_live.TryGetValue(sessionId, out var live))
        {
            live = await ReviveAsync(sessionId, ct)
                   ?? throw new InvalidOperationException("That session can no longer be steered.");
        }

        var session = live.Session;
        var message = new GuidanceMessage
        {
            Text = guidance.Trim(),
            Source = source,
            Interrupted = interrupt,
        };

        session.Guidance = Published(session, session.Guidance, list => list.Add(message));
        AppendMessage(session, MessageRole.Guidance, message.Text);

        // Route one: on disk, readable by any agent without an MCP server configured.
        try
        {
            await _workspaces.WriteGuidanceAsync(session.WorkingDirectory, session.Guidance, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write guidance to the workspace for session {SessionId}", session.Id);
        }

        var turnInFlight = live.TurnCancellation is not null;

        if (turnInFlight && interrupt)
        {
            // Stop this turn only — the session and its queue stay alive.
            live.InterruptedForGuidance = true;
            await live.TurnCancellation!.CancelAsync();
            await QueueGuidanceTurnAsync(live);
        }
        else if (!turnInFlight)
        {
            await QueueGuidanceTurnAsync(live);
        }

        // Otherwise it stays pending: the agent can pull it over MCP mid-turn, and it is folded
        // into the front of the next turn regardless.
        await PersistAsync(session);
        return message;
    }

    public async Task<IReadOnlyList<GuidanceMessage>> TakeGuidanceAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        if (session is null)
            return [];

        var pending = session.Guidance.Where(g => g.Status == GuidanceStatus.Pending).ToList();
        if (pending.Count == 0)
            return [];

        foreach (var message in pending)
        {
            message.Status = GuidanceStatus.Delivered;
            message.DeliveredAt = DateTimeOffset.UtcNow;
        }

        await PersistAsync(session);
        return pending;
    }

    private async Task QueueGuidanceTurnAsync(LiveSession live) =>
        await QueueContinuationAsync(live, GuidanceContinuationPrompt);

    /// <summary>Prompt used to carry the conversation on once a handover has just taken effect.</summary>
    private const string HandoverContinuationPrompt =
        "The model handover just took effect. Carry on with the conversation on the new model.";

    /// <summary>
    /// Queues a turn with no message of its own — guidance arriving idle, or a handover that just
    /// took effect — so the session keeps going rather than sitting there waiting for the person to
    /// send something.
    /// </summary>
    private async Task QueueContinuationAsync(LiveSession live, string prompt)
    {
        Interlocked.Increment(ref live.QueuedTurns);
        if (live.Idle.Task.IsCompleted)
            live.Idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await live.Turns.Writer.WriteAsync(prompt, CancellationToken.None);
    }

    /// <summary>Moves outstanding guidance to the top of the prompt about to be sent.</summary>
    private static string ApplyPendingGuidance(ChatSession session, string prompt)
    {
        var pending = session.Guidance
            .Where(g => g.Status == GuidanceStatus.Pending)
            .ToList();

        if (pending.Count == 0)
            return prompt;

        foreach (var message in pending)
        {
            message.Status = GuidanceStatus.Applied;
            message.DeliveredAt ??= DateTimeOffset.UtcNow;
        }

        var block = string.Join(Environment.NewLine, pending.Select(g => $"- {g.Text}"));
        return $"""
                [Guidance — this overrides earlier instructions where they conflict]
                {block}

                {prompt}
                """;
    }

    public async Task CancelAsync(string sessionId)
    {
        if (!_live.TryGetValue(sessionId, out var live))
            return;

        await live.Cancellation.CancelAsync();
        live.Turns.Writer.TryComplete();
    }

    public async Task<ChatSession?> SetStatusAsync(
        string sessionId,
        SessionStatus status,
        CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        if (session is null)
            return null;

        // A live pump owns the status while it runs and would write over anything set here, so the
        // run is stopped and given a moment to land before the operator's choice is applied. The
        // grace is short: the point is to settle a session, not to wait on a CLI that is not coming
        // back.
        if (_live.TryGetValue(sessionId, out var live))
        {
            await CancelAsync(sessionId);

            try
            {
                // The pump task, not the idle signal: idle only says the queue has drained, and the
                // pump settles the session as Cancelled on its way out — after this call would have
                // written the operator's choice if it did not wait for that to happen first.
                await live.Pump.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Session {SessionId} did not stop within the grace period; forcing status anyway",
                    sessionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // However the run ended is not this call's problem; the status is being replaced.
                _logger.LogDebug(ex, "Session {SessionId} ended badly while being stopped", sessionId);
            }

            _live.TryRemove(sessionId, out _);
        }

        if (session.Status == status)
            return session;

        session.Status = status;

        if (status is SessionStatus.Completed or SessionStatus.Failed or SessionStatus.Cancelled)
            session.EndedAt ??= DateTimeOffset.UtcNow;
        else
            session.EndedAt = null;

        await PersistAsync(session);
        return session;
    }

    public async Task<ChatSession?> CloseAsync(string sessionId, string? reason = null, CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        if (session is null)
            return null;

        if (session.IsClosed)
            return session;

        // Completing the writer rather than cancelling: the pump drains what it has, ends its loop
        // normally and hands the workspace back, where a cancel would settle the run as Cancelled.
        if (_live.TryRemove(sessionId, out var live))
        {
            live.Turns.Writer.TryComplete();

            // A turn in flight still owns session.Status until it lands, and would otherwise write
            // over the closed state set below the moment it finishes — the same race SetStatusAsync
            // guards against. Waited for here, with the same short grace, rather than cancelled, so
            // the run is allowed to actually finish instead of settling as Cancelled.
            try
            {
                await live.Pump.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Session {SessionId} did not stop within the grace period; closing anyway", sessionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Session {SessionId} ended badly while being closed", sessionId);
            }
        }

        // A run that fell over keeps saying so. Everything else has now finished, whatever the pump
        // left it on while it waited for input that is never coming.
        if (session.Status is not (SessionStatus.Failed or SessionStatus.Cancelled))
            session.Status = SessionStatus.Completed;

        session.IsClosed = true;
        session.ClosedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        session.ClosedAt = DateTimeOffset.UtcNow;
        session.EndedAt ??= session.ClosedAt;

        await PersistAsync(session);
        return session;
    }

    /// <summary>Refuses anything that would put another turn on a session that is finished.</summary>
    private async Task EnsureOpenAsync(string sessionId, CancellationToken ct)
    {
        var session = await GetAsync(sessionId, ct);
        if (session is { IsClosed: true })
        {
            throw new InvalidOperationException(
                session.ClosedReason is { Length: > 0 } reason
                    ? $"This session is finished and read only: {reason}"
                    : "This session is finished and read only.");
        }
    }

    public async Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct = default) =>
        _live.TryGetValue(sessionId, out var live) ? live.Session : await _sessions.GetAsync(sessionId, ct);

    public async Task<IReadOnlyList<ChatSession>> GetAllAsync(CancellationToken ct = default)
    {
        var stored = await _sessions.GetAllAsync(ct);
        // The in-memory copy is always the fresher one for anything still running.
        return stored
            .Select(s => _live.TryGetValue(s.Id, out var live) ? live.Session : s)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    public async Task<bool> DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        if (_live.TryGetValue(sessionId, out var live))
        {
            _deleting[sessionId] = 0;
            await CancelAsync(sessionId);

            try
            {
                await live.Pump.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Session {SessionId} did not stop before deletion", sessionId);
            }
        }

        _live.TryRemove(sessionId, out _);
        var deleted = await _sessions.DeleteAsync(sessionId, ct);

        if (live is null || live.Pump.IsCompleted)
            _deleting.TryRemove(sessionId, out _);

        return deleted;
    }

    public async Task<ChatSession> RunToCompletionAsync(
        StartSessionRequest request,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var session = await StartAsync(request, ct);
        if (!_live.TryGetValue(session.Id, out var live))
            return session;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await live.Idle.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(session.Id);
            session.Status = SessionStatus.Cancelled;
            session.LastError = ct.IsCancellationRequested ? "Cancelled." : "Timed out waiting for the agent.";
            await PersistAsync(session);
        }

        return session;
    }

    private async Task PumpAsync(LiveSession live)
    {
        var session = live.Session;
        try
        {
            while (await live.Turns.Reader.WaitToReadAsync(live.Cancellation.Token))
            {
                while (live.Turns.Reader.TryRead(out var prompt))
                {
                    var handoverAlreadyRequested = session.HandoverRequested;
                    await RunTurnAsync(live, prompt);

                    // The agent asked to change model mid-turn: the switch only takes effect on the
                    // next turn, so queue one straight away rather than leaving the conversation
                    // sitting there until the person sends something themselves.
                    if (!handoverAlreadyRequested && session.HandoverRequested)
                        await QueueContinuationAsync(live, HandoverContinuationPrompt);

                    if (Interlocked.Decrement(ref live.QueuedTurns) <= 0)
                    {
                        if (session.Status is not (SessionStatus.Failed or SessionStatus.Cancelled))
                            session.Status = SessionStatus.AwaitingInput;
                        await PersistAsync(session);
                        live.Idle.TrySetResult();
                    }

                    // The agent asked to end the conversation, and the turn that asked has now
                    // finished streaming: close the session so nothing further gets queued into it.
                    if (session.AgentEndConversationRequested && !session.IsClosed)
                    {
                        await CloseAsync(session.Id, "Ended by the agent.");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            session.Status = SessionStatus.Cancelled;
            session.EndedAt = DateTimeOffset.UtcNow;
            await PersistAsync(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId} pump failed", session.Id);
            session.Status = SessionStatus.Failed;
            session.LastError = ex.Message;
            await PersistAsync(session);
        }
        finally
        {
            live.Idle.TrySetResult();
            await ReleaseWorkspaceAsync(live);
            _deleting.TryRemove(session.Id, out _);
        }
    }

    /// <summary>Hands the worktree back once the session is finished with it.</summary>
    private async Task ReleaseWorkspaceAsync(LiveSession live)
    {
        if (live.Session.IsLive)
            return;

        try
        {
            await _workspaces.ReleaseAsync(live.Workspace, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not release the workspace for session {SessionId}", live.Session.Id);
        }
    }

    private async Task RunTurnAsync(LiveSession live, string prompt)
    {
        var session = live.Session;
        var agent = live.Agent;

        // The opening turns of a session can run on a different model from the rest, so this is
        // resolved per turn rather than once when the session started, and before anything is said
        // so every line of the turn can record what produced it.
        var choice = ModelSchedule.For(agent, session);

        // Where this turn's lines start, so the answers it produces can be counted at the end of it.
        var opened = session.Messages.Count;

        AppendMessage(session, MessageRole.User, prompt, model: choice.Model, effort: choice.Effort);

        // Said before the count moves, so the first message of a session — which has nothing behind
        // it to have changed from — is not announced as a change.
        AnnounceModelChange(session, choice);

        // A turn is a message: the one you just sent counts now, and whatever the agent says back
        // counts when it has been said. The model for this turn was chosen a line above, off what
        // had happened before you sent it.
        session.TurnCount++;
        session.ModelInUse = choice.Model;
        session.EffortInUse = choice.Effort;
        session.Status = SessionStatus.Starting;
        session.StartedAt ??= DateTimeOffset.UtcNow;
        Notify(session);

        await _concurrency.WaitAsync(live.Cancellation.Token);
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(live.Cancellation.Token);
        turnCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.TurnTimeoutMinutes)));
        live.TurnCancellation = turnCts;

        // Anything steered in before this turn started belongs at the top of the prompt.
        var effectivePrompt = ApplyPendingGuidance(session, prompt);

        // After a compaction the CLI has no history, so the summary is the context.
        if (session.ProviderSessionId is null && !string.IsNullOrWhiteSpace(session.Summary))
        {
            effectivePrompt = $"""
                               [Summary of the conversation so far]
                               {session.Summary}

                               {effectivePrompt}
                               """;
        }

        var assistant = AppendMessage(session, MessageRole.Agent, string.Empty, streaming: true,
            model: choice.Model, effort: choice.Effort);
        session.Status = SessionStatus.Running;
        Notify(session);

        try
        {
            // Rewritten every turn so servers added mid-conversation are picked up.
            var mcpServerNames = await _workspaces.MaterialiseMcpAsync(
                agent, session.WorkingDirectory, session.McpServerIds, turnCts.Token);

            var cli = await _clis.ResolveAsync(agent.Provider, agent.CliProviderId, turnCts.Token);
            session.CliProviderName = agent.Provider == AiProvider.Custom ? cli.DisplayName : null;
            // Composed per turn, so token tactics switched on or off mid-conversation are in force
            // from the next message without anything being restarted.
            var systemPrompt = await _workspaces.ComposeSystemPromptAsync(
                agent, session.ProjectId, session.Id, TokenMinimisation.For(agent, session),
                HandoverTarget(agent, session, choice), turnCts.Token);

            if (session.UseLlmForPromptingTips)
                systemPrompt = $"{systemPrompt}\n\n{LlmPromptingTipInstruction}";

            // Re-resolved each turn so moving a project onto another account takes effect straight away.
            var account = await _accounts.ResolveAsync(agent, session.ProjectId, turnCts.Token);
            session.AccountId = account.AccountId;
            session.AccountName = account.Name;
            var request = new TurnRequest
            {
                Prompt = effectivePrompt,
                WorkingDirectory = session.WorkingDirectory,
                PermissionMode = session.PermissionMode,
                Model = choice.Model,
                Effort = choice.Effort,
                SystemPrompt = systemPrompt,
                ResumeSessionId = session.ProviderSessionId,
                HomeDirectory = account.HomePath,
                FallbackHomeDirectory = account.Fallback?.HomePath,
                McpServerNames = mcpServerNames,
                AllowedTools = await AllowedToolsAsync(session, turnCts.Token),
                Environment = agent.Environment,
                ExtraArguments = agent.ExtraArguments,
            };

            await foreach (var evt in cli.RunTurnAsync(request, turnCts.Token))
            {
                switch (evt.Kind)
                {
                    case AgentEventKind.Text:
                        assistant.Content += evt.Text;

                        // Checked against the whole line so far, because the marker arrives split
                        // across however many chunks the CLI streams it in.
                        if (!session.HandoverRequested && WantsHandover(assistant.Content))
                            ApplyHandoverRequest(session, agent, choice);

                        if (!session.AgentEndConversationRequested && WantsEndConversation(assistant.Content))
                            session.AgentEndConversationRequested = true;

                        Notify(session);
                        break;

                    case AgentEventKind.Tool:
                        var tool = AppendMessage(session, MessageRole.Tool, string.IsNullOrEmpty(evt.ToolName)
                            ? evt.Text
                            : $"{evt.ToolName} {evt.Text}".Trim());
                        tool.ToolCallId = evt.ToolCallId;
                        session.ToolCallCount++;

                        // A call that writes to a file carries the change itself, which is worth
                        // showing in brief: what an agent did to the code is the part of a turn a
                        // reader most wants to check, and prose about it is not evidence.
                        if (evt.Edit is { } edit)
                        {
                            var change = FileChangeSummary.Abridge(edit);
                            tool.FilePath = edit.Path;
                            tool.LinesAdded = change.Added;
                            tool.LinesRemoved = change.Removed;
                            tool.Diff = change.Diff;
                        }

                        // Close the current bubble so the tool call lands between prose, not inside
                        // it, and open the next one after the call rather than before it — prose
                        // that follows a tool call belongs below it in the transcript.
                        if (assistant.Content.Length > 0)
                        {
                            assistant.IsStreaming = false;
                            assistant = AppendMessage(session, MessageRole.Agent, string.Empty, streaming: true,
                                model: choice.Model, effort: choice.Effort);
                        }

                        Notify(session);
                        break;

                    case AgentEventKind.ToolCompleted:
                        // The CLIs report a result long after the call, and out of order with the
                        // prose in between, so the line is found by id rather than by position.
                        if (session.Messages.LastOrDefault(m =>
                                m.Role == MessageRole.Tool &&
                                m.ToolCallId is { Length: > 0 } &&
                                m.ToolCallId == evt.ToolCallId) is { } called)
                        {
                            called.DurationMs = evt.DurationMs
                                ?? (int)Math.Max(0, (DateTimeOffset.UtcNow - called.Timestamp).TotalMilliseconds);
                            Notify(session);
                        }

                        break;

                    case AgentEventKind.Usage:
                        // One of these arrives per request to the model, so a turn that uses tools
                        // reports several and they add up rather than replace each other.
                        if (evt.Usage is { IsEmpty: false } usage)
                        {
                            Count(assistant, usage);
                            Count(session, usage);
                            Notify(session);
                        }

                        break;

                    case AgentEventKind.SessionId:
                        session.ProviderSessionId = evt.Text;
                        break;

                    case AgentEventKind.Result:
                        if (assistant.Content.Length == 0 && !string.IsNullOrWhiteSpace(evt.Text))
                            assistant.Content = evt.Text;

                        // Some CLIs only ever report the answer here, so the marker is looked for
                        // again rather than only in the streamed prose.
                        if (!session.HandoverRequested && WantsHandover(assistant.Content))
                            ApplyHandoverRequest(session, agent, choice);

                        if (!session.AgentEndConversationRequested && WantsEndConversation(assistant.Content))
                            session.AgentEndConversationRequested = true;

                        break;

                    case AgentEventKind.Error:
                        AppendMessage(session, MessageRole.Error, evt.Text);
                        session.LastError = evt.Text;
                        Notify(session);
                        break;

                    case AgentEventKind.PermissionDenied:
                        RecordApproval(session, evt.ToolName, evt.Text);
                        Notify(session);
                        break;

                    case AgentEventKind.Log:
                        _logger.LogDebug("[{Provider}] {Text}", agent.Provider, evt.Text);
                        break;
                }
            }

            session.Status = session.LastError is null ? SessionStatus.AwaitingInput : SessionStatus.Failed;
        }
        catch (OperationCanceledException) when (live.InterruptedForGuidance)
        {
            live.InterruptedForGuidance = false;
            AppendMessage(session, MessageRole.System, "Turn stopped so new guidance could take effect.");
            session.Status = SessionStatus.AwaitingInput;
        }
        catch (OperationCanceledException) when (live.Cancellation.IsCancellationRequested)
        {
            AppendMessage(session, MessageRole.System, "Stopped by user.");
            session.Status = SessionStatus.Cancelled;
        }
        catch (OperationCanceledException)
        {
            AppendMessage(session, MessageRole.Error, "The turn exceeded its time limit and was stopped.");
            session.Status = SessionStatus.Failed;
            session.LastError = "Turn timed out.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Turn failed for session {SessionId}", session.Id);
            AppendMessage(session, MessageRole.Error, ex.Message);
            session.Status = SessionStatus.Failed;
            session.LastError = ex.Message;
        }
        finally
        {
            assistant.IsStreaming = false;
            if (string.IsNullOrWhiteSpace(assistant.Content))
                session.Messages = Published(session, session.Messages, list => list.Remove(assistant));

            live.TurnCancellation = null;
            _concurrency.Release();
            session.EndedAt = DateTimeOffset.UtcNow;

            // The agent's half of the exchange, which is one message per answer it wrote — a turn
            // interrupted by tool calls is several. Tool calls themselves are not messages and are
            // not counted, and neither is an answer that never arrived. Counted here rather than
            // after the persist below it, which used to save — and tell the UI about — a session
            // whose count was behind what had actually run.
            session.TurnCount += session.Messages
                .Skip(opened)
                .Count(m => m.Role == MessageRole.Agent && m.Content.Length > 0);

            await PersistAsync(session);
        }

        await SummariseIfDueAsync(live);
        await DeriveGoalIfDueAsync(live);
    }

    private const string LlmPromptingTipInstruction = """
        After answering a user's request, add a brief section headed "Prompting tip" with one
        practical suggestion for making their next prompt clearer, more focused, or less costly.
        Keep it to one sentence, and omit it when the message is only a trivial acknowledgement.
        Do not let this tip prevent you from completing the requested work.
        """;

    /// <summary>Turns a session must reach before an unset goal is worth guessing at.</summary>
    private const int GoalDerivationTurnThreshold = 2;

    /// <summary>
    /// Fills in <see cref="ChatSession.Goal"/> once there is enough conversation to guess at it. Runs
    /// the same way as <see cref="SummariseIfDueAsync"/> — a plan-mode turn on the live agent, so the
    /// guess is grounded in what was actually said rather than a canned heuristic. Never overwrites a
    /// goal a person or the agent already chose; that is a stronger signal than a guess ever is.
    /// </summary>
    private async Task DeriveGoalIfDueAsync(LiveSession live)
    {
        var session = live.Session;

        if (session.Status is SessionStatus.Cancelled or SessionStatus.Failed)
            return;

        if (!string.IsNullOrWhiteSpace(session.Goal) || !string.IsNullOrWhiteSpace(session.ProposedGoal))
            return;

        if (session.TurnCount < GoalDerivationTurnThreshold)
            return;

        // Already tried at this point in the conversation and got nothing usable — retried once
        // there has been more to go on, not on every single turn from here on.
        if (session.LastGoalAttemptTurn >= session.TurnCount)
            return;

        var settings = await _globalSettings.GetAsync(GlobalSettings.WellKnownId, CancellationToken.None);
        if (!(settings?.EnableGoal ?? true))
            return;

        try
        {
            await _concurrency.WaitAsync(live.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(live.Cancellation.Token);
            cts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.TurnTimeoutMinutes)));

            var account = await _accounts.ResolveAsync(live.Agent, session.ProjectId, cts.Token);
            var goal = new System.Text.StringBuilder();

            var cli = await _clis.ResolveAsync(live.Agent.Provider, live.Agent.CliProviderId, cts.Token);
            var choice = ModelSchedule.For(live.Agent, session);

            await foreach (var evt in cli.RunTurnAsync(new TurnRequest
            {
                Prompt = DefaultGoalPrompt,
                WorkingDirectory = session.WorkingDirectory,
                PermissionMode = PermissionMode.Plan,
                Model = choice.Model,
                Effort = choice.Effort,
                ResumeSessionId = session.ProviderSessionId,
                HomeDirectory = account.HomePath,
                Environment = live.Agent.Environment,
            }, cts.Token))
            {
                if (evt.Kind is AgentEventKind.Text or AgentEventKind.Result)
                    goal.Append(evt.Text);
                else if (evt is { Kind: AgentEventKind.Usage, Usage: { IsEmpty: false } used })
                    Count(session, used);
            }

            session.LastGoalAttemptTurn = session.TurnCount;

            var text = goal.ToString().Trim();
            if (text.Length == 0)
                return;

            // Never adopted automatically — a guess only becomes the goal the session is actually
            // held to once a person confirms or edits it from the sidebar. Shown only in the sidebar's
            // Goal card (Chat.razor) — not also appended to the transcript, to avoid showing the same
            // guess twice.
            session.ProposedGoal = text;

            await PersistAsync(session);
        }
        catch (OperationCanceledException)
        {
            // A guessed goal is a convenience; losing one must not disturb the session.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not derive a goal for session {SessionId}", session.Id);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private const string DefaultGoalPrompt =
        """
        In one short sentence, state the goal of this conversation — what the user is actually trying
        to achieve. Answer with the sentence alone, no preamble, no markdown. Do not edit anything —
        this is a read-only question.
        """;

    /// <summary>
    /// Rolls the conversation up when the project's turn threshold is reached. The summary is asked
    /// of the same agent, so it is written with full knowledge of the work; compacting afterwards
    /// clears the provider's conversation id so the next turn starts from the summary alone.
    /// </summary>
    private async Task SummariseIfDueAsync(LiveSession live)
    {
        var session = live.Session;

        if (session.ProjectId is null || session.Status is SessionStatus.Cancelled or SessionStatus.Failed)
            return;

        var project = await _projects.GetAsync(session.ProjectId, CancellationToken.None);
        if (project is null || project.SummariseAfterTurns <= 0)
            return;

        if (session.TurnCount - session.LastSummarisedTurn < project.SummariseAfterTurns)
            return;

        var instruction = string.IsNullOrWhiteSpace(project.SummaryPrompt)
            ? DefaultSummaryPrompt
            : project.SummaryPrompt!;

        try
        {
            // Taken outside the try below so a cancelled wait cannot release a slot it never held.
            await _concurrency.WaitAsync(live.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(live.Cancellation.Token);
            cts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.TurnTimeoutMinutes)));

            var account = await _accounts.ResolveAsync(live.Agent, session.ProjectId, cts.Token);
            var summary = new System.Text.StringBuilder();

            var summariser = await _clis.ResolveAsync(live.Agent.Provider, live.Agent.CliProviderId, cts.Token);

            // Whatever the conversation is running on now, which after a handover is the cheaper
            // model — right for a summary.
            var choice = ModelSchedule.For(live.Agent, session);

            await foreach (var evt in summariser.RunTurnAsync(new TurnRequest
            {
                Prompt = instruction,
                WorkingDirectory = session.WorkingDirectory,
                PermissionMode = PermissionMode.Plan,
                Model = choice.Model,
                Effort = choice.Effort,
                ResumeSessionId = session.ProviderSessionId,
                HomeDirectory = account.HomePath,
                Environment = live.Agent.Environment,
            }, cts.Token))
            {
                if (evt.Kind is AgentEventKind.Text or AgentEventKind.Result)
                    summary.Append(evt.Text);
                else if (evt is { Kind: AgentEventKind.Usage, Usage: { IsEmpty: false } used })
                    Count(session, used);
            }

            // Marked whether or not anything came back, so a summary that fails is not retried on
            // every turn from here on.
            session.LastSummarisedTurn = session.TurnCount;

            var text = summary.ToString().Trim();
            if (text.Length == 0)
                return;

            session.Summary = text;
            session.SummaryCount++;
            session.LastSummarisedAt = DateTimeOffset.UtcNow;

            AppendMessage(session, MessageRole.Summary, text, model: choice.Model, effort: choice.Effort);

            if (project.CompactAfterSummary)
            {
                // Dropping the id is the compaction: the next turn opens a fresh CLI conversation
                // and the summary above becomes its context.
                session.ProviderSessionId = null;
                AppendMessage(session, MessageRole.System,
                    $"Context compacted after {session.TurnCount} messages. The summary above carries forward.");
            }

            await PersistAsync(session);
        }
        catch (OperationCanceledException)
        {
            // A summary is a convenience; losing one must not disturb the session.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not summarise session {SessionId}", session.Id);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private const string DefaultSummaryPrompt =
        """
        Summarise this conversation so a fresh session could pick the work up without the transcript.
        Cover: what was asked for, what you changed and where, decisions made and why, anything still
        outstanding, and any constraint you discovered. Be specific about file paths and names. Do not
        edit anything — this is a summary only.
        """;

    /// <summary>Rebuilds in-memory state for a stored session so it can accept another turn.</summary>
    private async Task<LiveSession?> ReviveAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(sessionId, ct);
        if (session is null)
            return null;

        var agent = await _agents.GetAsync(session.AgentId, ct);
        if (agent is null)
            return null;

        agent = await ApplyProjectProviderAsync(agent, session.ProjectId, ct);

        if (string.IsNullOrWhiteSpace(session.WorkingDirectory) || !Directory.Exists(session.WorkingDirectory))
        {
            var prepared = await _workspaces.PrepareAsync(agent, session.Id, session.ProjectId, ct);
            session.WorkingDirectory = prepared.Path;
        }

        var candidate = new LiveSession
        {
            Session = session,
            Turns = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true }),
            Cancellation = new CancellationTokenSource(),
            Workspace = new SessionWorkspace(session.WorkingDirectory, session.RepositoryId, null, session.ProjectId),
            Agent = agent,
        };

        var live = _live.GetOrAdd(sessionId, candidate);
        if (live.Pump.IsCompleted)
            live.Pump = Task.Run(() => PumpAsync(live), CancellationToken.None);

        return live;
    }

    private static ChatMessage AppendMessage(
        ChatSession session,
        MessageRole role,
        string content,
        bool streaming = false,
        string? model = null,
        string? effort = null)
    {
        var message = new ChatMessage
        {
            Role = role,
            Content = content,
            IsStreaming = streaming,
            Model = model,
            Effort = effort,
        };

        session.Messages = Published(session, session.Messages, list => list.Add(message));
        return message;
    }

    /// <summary>
    /// Says in the transcript when a turn runs on something other than the last one did. The
    /// handover from an opening model is the usual cause and changing the model mid-conversation is
    /// the other; either way, two answers written by different models sit next to each other with
    /// nothing to tell them apart unless the change is recorded where it happened.
    /// </summary>
    /// <summary>
    /// What an agent writes to move itself onto the cheaper model. Matched case-insensitively, and
    /// anywhere in an answer: a model asked for a marker on its own line will sometimes put it at
    /// the end of a sentence, and refusing that would make the feature unreliable for no gain.
    /// </summary>
    public const string HandoverMarker = "[CHANGE MODEL]";

    /// <summary>Whether an answer carries the change-model marker.</summary>
    private static bool WantsHandover(string text) =>
        text.Contains(HandoverMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What an agent writes when the task is genuinely finished and nothing further is expected from
    /// either side. Matched the same way as <see cref="HandoverMarker"/>, for the same reason.
    /// </summary>
    public const string EndConversationMarker = "[END CONVERSATION]";

    /// <summary>Whether an answer carries the end-conversation marker.</summary>
    private static bool WantsEndConversation(string text) =>
        text.Contains(EndConversationMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The cheaper model this conversation could move to on request, or null when there is nowhere
    /// to go. First choice is the handover somebody configured — that pair is a stated intent about
    /// this agent. Failing that, the next model down the list configured for the CLI, which is
    /// written strongest first. A conversation on the CLI's own default model has neither, and is
    /// left alone rather than moved somewhere nobody chose.
    /// </summary>
    private string? HandoverTarget(Agent agent, ChatSession session, ModelChoice choice)
    {
        if (session.HandoverRequested)
            return null;

        var settled = ModelSchedule.Settled(agent, session);
        if (choice.IsOpening && settled.Model is { Length: > 0 } && settled.Model != choice.Model)
            return settled.Model;

        return ModelSchedule.NextDown(choice.Model, ModelsFor(agent));
    }

    private IReadOnlyList<string> ModelsFor(Agent agent) => agent.Provider switch
    {
        AiProvider.Claude => _options.ClaudeModels,
        AiProvider.Codex => _options.CodexModels,
        AiProvider.Opencoder => _options.OpencoderModels,
        // A user-defined CLI keeps its models on its own definition, which this does not read. Its
        // agents can still hand over, through a handover configured on the agent.
        _ => [],
    };

    /// <summary>
    /// Moves the conversation onto the cheaper model because the agent asked. The turn saying so is
    /// already running on the model it is running on and cannot be switched under it, so this lands
    /// on the next turn — which the transcript says, rather than letting the reader assume the
    /// answer they are reading came from the cheaper one.
    /// </summary>
    private void ApplyHandoverRequest(ChatSession session, Agent agent, ModelChoice choice)
    {
        if (HandoverTarget(agent, session, choice) is not { } target)
            return;

        session.HandoverRequested = true;

        // Ending the opening is enough when a handover was configured. When it was not, the target
        // came from the model list and has to be written down, or nothing would change.
        if (ModelSchedule.For(agent, session).Model != target)
            session.Model = target;

        AppendMessage(session, MessageRole.System,
            $"The agent asked to change model. The rest of the conversation runs on {target}, from the next turn.");

        _logger.LogInformation("Session {SessionId} handed over to {Model} at the agent's request", session.Id, target);
    }

    private static void AnnounceModelChange(ChatSession session, ModelChoice choice)
    {
        if (session.TurnCount == 0)
            return;

        var before = ModelSchedule.Describe(session.ModelInUse, session.EffortInUse);
        var after = ModelSchedule.Describe(choice.Model, choice.Effort);

        if (before == after)
            return;

        AppendMessage(session, MessageRole.System, $"Model has changed from {before} to {after}.");
    }

    private static void Count(ChatMessage message, TokenUsage usage)
    {
        message.InputTokens += usage.Input;
        message.OutputTokens += usage.Output;
        message.CacheTokens += usage.CacheRead + usage.CacheWrite;
    }

    private static void Count(ChatSession session, TokenUsage usage)
    {
        session.InputTokens += usage.Input;
        session.OutputTokens += usage.Output;
        session.CacheTokens += usage.CacheRead + usage.CacheWrite;
    }

    /// <summary>
    /// Applies a change to a copy and hands back the copy, leaving the original untouched.
    ///
    /// A turn runs on a background pump while a browser circuit renders the same session object.
    /// Appending to the very list the renderer is enumerating throws "collection was modified" and
    /// takes the circuit down with it — a race that streaming made near-certain, because the
    /// transcript now changes many times a second. Readers keep enumerating the list they started
    /// on, which nothing will ever mutate again.
    ///
    /// The lock covers writers only: guidance and approvals arrive on the UI thread while the pump
    /// is writing, and two copies made from the same starting point would lose one of the entries.
    /// </summary>
    private static List<T> Published<T>(ChatSession session, List<T> current, Action<List<T>> change)
    {
        lock (session)
        {
            var copy = new List<T>(current);
            change(copy);
            return copy;
        }
    }

    /// <summary>
    /// The standing allow-list plus whatever the operator approved for this session. Approvals are
    /// second so a rule granted by hand wins nothing it did not already have, and duplicates are
    /// dropped because some CLIs treat a repeated rule as a parse error.
    /// </summary>
    private async Task<IReadOnlyList<string>> AllowedToolsAsync(ChatSession session, CancellationToken ct)
    {
        var rules = _options.DefaultAllowedTools.Concat(session.AllowedTools);

        var settings = await _globalSettings.GetAsync(GlobalSettings.WellKnownId, ct);
        if (settings?.EnableWebTools ?? true)
            rules = rules.Concat(["WebFetch", "WebSearch"]);

        return [.. rules.Distinct()];
    }

    private static string BuildTitle(string prompt)
    {
        var firstLine = prompt.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Session";
        return firstLine.Length <= 60 ? firstLine : firstLine[..57] + "...";
    }

    private async Task PersistAsync(ChatSession session)
    {
        if (_deleting.ContainsKey(session.Id))
            return;

        try
        {
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await _sessions.UpsertAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist session {SessionId}", session.Id);
        }
        Notify(session);
    }

    private void Notify(ChatSession session)
    {
        try
        {
            SessionUpdated?.Invoke(session);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A session subscriber threw");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var live in _live.Values)
        {
            await live.Cancellation.CancelAsync();
            live.Turns.Writer.TryComplete();
        }
        _concurrency.Dispose();
    }
}
