using System.Collections.Concurrent;
using System.Threading.Channels;
using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Domain.Agents;
using AiShop.Domain.Projects;
using AiShop.Domain.Providers;
using AiShop.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiShop.Application.Sessions;

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
    private readonly IEntityStore<ChatSession> _sessions;
    private readonly IEntityStore<Agent> _agents;
    private readonly IProviderCliRegistry _clis;
    private readonly IWorkspaceService _workspaces;
    private readonly IAccountService _accounts;
    private readonly IEntityStore<Project> _projects;
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
        IOptions<OrchestratorOptions> options,
        ILogger<SessionManager> logger)
    {
        _sessions = sessions;
        _agents = agents;
        _clis = clis;
        _workspaces = workspaces;
        _accounts = accounts;
        _projects = projects;
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

        var session = new ChatSession
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? BuildTitle(request.Prompt) : request.Title!,
            AgentId = agent.Id,
            AgentName = agent.Name,
            Provider = agent.Provider,
            CliProviderId = agent.CliProviderId,
            PermissionMode = request.PermissionMode ?? agent.PermissionMode,
            Trigger = request.Trigger,
            ProjectId = request.ProjectId ?? agent.ProjectId,
            McpServerIds = [.. request.McpServerIds],
            WorkflowRunId = request.WorkflowRunId,
            ScheduleId = request.ScheduleId,
            Status = SessionStatus.Starting,
        };

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
        await live.Turns.Writer.WriteAsync(request.Prompt, CancellationToken.None);
        live.Pump = Task.Run(() => PumpAsync(live), CancellationToken.None);

        Notify(session);
        return session;
    }

    public async Task SendAsync(string sessionId, string message, CancellationToken ct = default)
    {
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
            SystemPrompt = agent.SystemPrompt,
            PermissionMode = agent.PermissionMode,
            ProjectId = agent.ProjectId,
            AccountId = agent.AccountId,
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
            session.AllowedTools.Add(rule);

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

        session.Approvals.Add(new ToolApproval
        {
            ToolName = toolName,
            Detail = trimmed,
            SuggestedRule = SuggestRule(toolName, trimmed),
        });
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

        session.Guidance.Add(message);
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

    private async Task QueueGuidanceTurnAsync(LiveSession live)
    {
        Interlocked.Increment(ref live.QueuedTurns);
        if (live.Idle.Task.IsCompleted)
            live.Idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await live.Turns.Writer.WriteAsync(GuidanceContinuationPrompt, CancellationToken.None);
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
        await CancelAsync(sessionId);
        _live.TryRemove(sessionId, out _);
        return await _sessions.DeleteAsync(sessionId, ct);
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
                    await RunTurnAsync(live, prompt);
                    if (Interlocked.Decrement(ref live.QueuedTurns) <= 0)
                    {
                        if (session.Status is not (SessionStatus.Failed or SessionStatus.Cancelled))
                            session.Status = SessionStatus.AwaitingInput;
                        await PersistAsync(session);
                        live.Idle.TrySetResult();
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

        AppendMessage(session, MessageRole.User, prompt);
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

        var assistant = AppendMessage(session, MessageRole.Agent, string.Empty, streaming: true);
        session.Status = SessionStatus.Running;
        Notify(session);

        try
        {
            // Rewritten every turn so servers added mid-conversation are picked up.
            var mcpServerNames = await _workspaces.MaterialiseMcpAsync(
                agent, session.WorkingDirectory, session.McpServerIds, turnCts.Token);

            var cli = await _clis.ResolveAsync(agent.Provider, agent.CliProviderId, turnCts.Token);
            session.CliProviderName = agent.Provider == AiProvider.Custom ? cli.DisplayName : null;
            var systemPrompt = await _workspaces.ComposeSystemPromptAsync(agent, session.ProjectId, session.Id, turnCts.Token);

            // Re-resolved each turn so moving a project onto another account takes effect straight away.
            var account = await _accounts.ResolveAsync(agent, session.ProjectId, turnCts.Token);
            session.AccountId = account.AccountId;
            session.AccountName = account.Name;
            var request = new TurnRequest
            {
                Prompt = effectivePrompt,
                WorkingDirectory = session.WorkingDirectory,
                PermissionMode = session.PermissionMode,
                Model = agent.Model,
                Effort = agent.Effort,
                SystemPrompt = systemPrompt,
                ResumeSessionId = session.ProviderSessionId,
                HomeDirectory = account.HomePath,
                McpServerNames = mcpServerNames,
                AllowedTools = session.AllowedTools,
                Environment = agent.Environment,
                ExtraArguments = agent.ExtraArguments,
            };

            await foreach (var evt in cli.RunTurnAsync(request, turnCts.Token))
            {
                switch (evt.Kind)
                {
                    case AgentEventKind.Text:
                        assistant.Content += evt.Text;
                        Notify(session);
                        break;

                    case AgentEventKind.Tool:
                        // Close the current bubble so the tool call lands between prose, not inside it.
                        if (assistant.Content.Length > 0)
                        {
                            assistant.IsStreaming = false;
                            assistant = AppendMessage(session, MessageRole.Agent, string.Empty, streaming: true);
                        }
                        AppendMessage(session, MessageRole.Tool, string.IsNullOrEmpty(evt.ToolName)
                            ? evt.Text
                            : $"{evt.ToolName} {evt.Text}".Trim());
                        Notify(session);
                        break;

                    case AgentEventKind.SessionId:
                        session.ProviderSessionId = evt.Text;
                        break;

                    case AgentEventKind.Result:
                        if (assistant.Content.Length == 0 && !string.IsNullOrWhiteSpace(evt.Text))
                            assistant.Content = evt.Text;
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
                session.Messages.Remove(assistant);

            live.TurnCancellation = null;
            _concurrency.Release();
            session.EndedAt = DateTimeOffset.UtcNow;
            await PersistAsync(session);
        }

        session.TurnCount++;
        await SummariseIfDueAsync(live);
    }

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

        if (session.TurnCount == 0 || session.TurnCount % project.SummariseAfterTurns != 0)
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

            await foreach (var evt in summariser.RunTurnAsync(new TurnRequest
            {
                Prompt = instruction,
                WorkingDirectory = session.WorkingDirectory,
                PermissionMode = PermissionMode.Plan,
                Model = live.Agent.Model,
                Effort = live.Agent.Effort,
                ResumeSessionId = session.ProviderSessionId,
                HomeDirectory = account.HomePath,
                Environment = live.Agent.Environment,
            }, cts.Token))
            {
                if (evt.Kind is AgentEventKind.Text or AgentEventKind.Result)
                    summary.Append(evt.Text);
            }

            var text = summary.ToString().Trim();
            if (text.Length == 0)
                return;

            session.Summary = text;
            session.SummaryCount++;
            session.LastSummarisedAt = DateTimeOffset.UtcNow;

            AppendMessage(session, MessageRole.Summary, text);

            if (project.CompactAfterSummary)
            {
                // Dropping the id is the compaction: the next turn opens a fresh CLI conversation
                // and the summary above becomes its context.
                session.ProviderSessionId = null;
                AppendMessage(session, MessageRole.System,
                    $"Context compacted after {session.TurnCount} turns. The summary above carries forward.");
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

    private static ChatMessage AppendMessage(ChatSession session, MessageRole role, string content, bool streaming = false)
    {
        var message = new ChatMessage { Role = role, Content = content, IsStreaming = streaming };
        session.Messages.Add(message);
        return message;
    }

    private static string BuildTitle(string prompt)
    {
        var firstLine = prompt.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Session";
        return firstLine.Length <= 60 ? firstLine : firstLine[..57] + "...";
    }

    private async Task PersistAsync(ChatSession session)
    {
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
