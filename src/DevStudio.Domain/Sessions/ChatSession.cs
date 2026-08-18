using DevStudio.Domain.Agents;
using DevStudio.Domain.Common;
using DevStudio.Domain.Providers;

namespace DevStudio.Domain.Sessions;

public enum SessionStatus
{
    Pending = 0,
    Starting = 1,
    Running = 2,
    /// <summary>The CLI asked something and is blocked until a human answers.</summary>
    AwaitingInput = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
}

public enum MessageRole
{
    User = 0,
    Agent = 1,
    System = 2,
    Tool = 3,
    Error = 4,
    /// <summary>A steer from the operator or a manager agent, sent while work was already under way.</summary>
    Guidance = 5,
    /// <summary>A rolling summary the orchestrator asked for after a run of turns.</summary>
    Summary = 6,
}

public enum GuidanceStatus
{
    /// <summary>Recorded, but the agent has not seen it yet.</summary>
    Pending = 0,
    /// <summary>The agent pulled it mid-turn over MCP.</summary>
    Delivered = 1,
    /// <summary>Folded into the prompt at the start of a turn.</summary>
    Applied = 2,
}

/// <summary>
/// A course correction aimed at a session that is already running. A turn is one CLI invocation and
/// its prompt cannot be rewritten once it starts, so guidance reaches the agent by two routes: it is
/// written to GUIDANCE.md in the workspace and served over MCP for a running agent to pull, and it is
/// folded into the front of the next turn either way.
/// </summary>
public sealed class GuidanceMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Who sent it: "operator", or "agent:&lt;name&gt;" when it came in over MCP.</summary>
    public string Source { get; set; } = "operator";
    public GuidanceStatus Status { get; set; } = GuidanceStatus.Pending;
    public DateTimeOffset? DeliveredAt { get; set; }
    /// <summary>The turn in flight was stopped so the guidance could take effect immediately.</summary>
    public bool Interrupted { get; set; }
}

/// <summary>One line of conversation. Agent turns are appended incrementally while streaming.</summary>
public sealed class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>True while the CLI is still writing this message.</summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// The CLI's id for the tool call this line reports, so the event that finishes it can be
    /// matched back. Null for anything that is not a tool call.
    /// </summary>
    public string? ToolCallId { get; set; }

    /// <summary>
    /// How long the tool took, once it has finished. Measured from the call being announced to the
    /// CLI reporting a result, so it is wall-clock time including whatever the tool waited on.
    /// </summary>
    public int? DurationMs { get; set; }

    /// <summary>
    /// The model this line was produced on, and the thinking level it ran at. Recorded per message
    /// rather than per session because a conversation can change model part way through, and an
    /// answer is only worth comparing with another once you know what wrote each of them. Null on
    /// anything the orchestrator wrote itself.
    /// </summary>
    public string? Model { get; set; }

    public string? Effort { get; set; }

    /// <summary>Set when this line reports a change to a file rather than any other tool call.</summary>
    public string? FilePath { get; set; }

    /// <summary>Lines the change added and removed, for the one-line summary above the diff.</summary>
    public int? LinesAdded { get; set; }

    public int? LinesRemoved { get; set; }

    /// <summary>
    /// The change itself in brief: the edited lines, +/- prefixed and capped, so a reader can see
    /// what the agent did to a file without opening it or trusting the prose about it.
    /// </summary>
    public string? Diff { get; set; }

    /// <summary>
    /// Tokens spent producing this line, added to as the CLI reports each request it makes, so the
    /// number moves while the answer is still being written. Cached reads are kept separate: they
    /// dwarf everything else on a long conversation and would drown the part that varies.
    /// </summary>
    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int CacheTokens { get; set; }

    /// <summary>Everything the model was charged for on this line.</summary>
    public int TotalTokens => InputTokens + OutputTokens + CacheTokens;
}

public enum SessionTrigger
{
    Manual = 0,
    Schedule = 1,
    Workflow = 2,
    Queue = 3,
}

public enum ApprovalStatus
{
    Pending = 0,
    Allowed = 1,
    Denied = 2,
}

/// <summary>A tool call the CLI was not allowed to make, waiting on the operator.</summary>
public sealed class ToolApproval
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>The CLI's name for the tool, e.g. <c>Bash</c>.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>What the tool was asked to do, for the operator to read before deciding.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// The rule granting this in the CLI's allow-list syntax, e.g. <c>Bash(gh pr view:*)</c>. It
    /// covers the shape of the command rather than the exact string, so one approval carries.
    /// </summary>
    public string SuggestedRule { get; set; } = string.Empty;

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A link an agent hung on the session sidebar — e.g. "Open PR" pointing at the pull request it
/// just opened. Purely informational: the app never dereferences it.
/// </summary>
public sealed class SessionLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A single conversation with one CLI process. Many run concurrently.</summary>
public sealed class ChatSession : Entity
{
    public string Title { get; set; } = "Untitled session";
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;

    /// <summary>Parent conversation when this session is an underlying subagent.</summary>
    public string? ParentSessionId { get; set; }
    public AiProvider Provider { get; set; }

    /// <summary>Set for a user-defined CLI, with its name kept for display.</summary>
    public string? CliProviderId { get; set; }
    public string? CliProviderName { get; set; }
    public PermissionMode PermissionMode { get; set; }

    /// <summary>
    /// Model for this conversation, overriding the agent's. Null keeps the agent's choice, which is
    /// what every session started before anyone touched this holds.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>Thinking level for this conversation, overriding the agent's. Null keeps the agent's.</summary>
    public string? Effort { get; set; }

    /// <summary>Overrides the agent's opening model. Null keeps the agent's.</summary>
    public string? OpeningModel { get; set; }

    /// <summary>Overrides the agent's opening thinking level. Null keeps the agent's.</summary>
    public string? OpeningEffort { get; set; }

    /// <summary>
    /// Overrides how many opening turns the opening model covers. Null keeps the agent's; zero turns
    /// off a handover the agent would otherwise do.
    /// </summary>
    public int? OpeningTurns { get; set; }

    /// <summary>
    /// Token-saving tactics chosen for this conversation, overriding the agent's. Null — what every
    /// untouched chat holds — keeps the agent's selection. The prompt is composed fresh each turn,
    /// so switching one on or off mid-conversation applies from the next message.
    /// </summary>
    public TokenTactics? TokenMinimisation { get; set; }

    /// <summary>Whether this session asks the model to append prompting tips to its answers.</summary>
    public bool UseLlmForPromptingTips { get; set; }

    /// <summary>
    /// The agent asked to move to the cheaper model itself, by writing the change-model marker in an
    /// answer. It ends the opening early — a handover is otherwise a distance in turns, and the
    /// agent is better placed than a turn count to know when the thinking is finished. It is not
    /// undone by anything the agent does afterwards; only a person changing the model can.
    /// </summary>
    public bool HandoverRequested { get; set; }

    /// <summary>
    /// The agent asked to end the conversation itself, by writing the end-conversation marker in an
    /// answer. Closing the session mid-stream would cut the answer off, so this only records the
    /// request; the pump closes the session once the turn that made it has finished.
    /// </summary>
    public bool AgentEndConversationRequested { get; set; }

    /// <summary>
    /// Tool calls made across the whole conversation. Counted as they happen rather than totted up
    /// from the transcript, which a compaction empties — the same reason the token counters live
    /// here.
    /// </summary>
    public int ToolCallCount { get; set; }

    /// <summary>
    /// The model the last turn actually ran on, recorded so the chat can say which side of a handover
    /// it is on rather than making the reader work it out.
    /// </summary>
    public string? ModelInUse { get; set; }

    /// <summary>The thinking level that went with <see cref="ModelInUse"/>.</summary>
    public string? EffortInUse { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Pending;
    public SessionTrigger Trigger { get; set; } = SessionTrigger.Manual;

    /// <summary>Directory the CLI runs in — a worktree path when the agent isolates its work.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;
    public string? ProjectId { get; set; }

    /// <summary>The logged-in account this session ran under.</summary>
    public string? AccountId { get; set; }
    public string? AccountName { get; set; }

    public string? RepositoryId { get; set; }
    public string? WorktreeId { get; set; }

    /// <summary>Provider-side session id, captured from CLI output so the chat can be resumed.</summary>
    public string? ProviderSessionId { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];

    /// <summary>
    /// Tools the CLI refused to run for want of permission. Running headless there is nobody at the
    /// terminal to answer, so the request is parked here for the operator to decide in the UI.
    /// </summary>
    public List<ToolApproval> Approvals { get; set; } = [];

    /// <summary>
    /// Permission rules granted from the UI, in the CLI's own allow-list syntax. They accumulate for
    /// the life of the session and are passed on every subsequent turn.
    /// </summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>
    /// Messages exchanged, which is what the project's summarisation threshold and the model
    /// handover both count. One message you send is a turn and one answer the agent writes is
    /// another, so an ordinary exchange is two; tool calls and everything the orchestrator says for
    /// itself are not messages and are not counted.
    /// </summary>
    public int TurnCount { get; set; }

    /// <summary>
    /// The count when this session was last summarised. The count no longer moves one at a time —
    /// an answer broken up by tool calls adds several — so being due is a distance from the last
    /// one rather than an exact multiple, which could be stepped straight over.
    /// </summary>
    public int LastSummarisedTurn { get; set; }

    /// <summary>
    /// Tokens this conversation has spent across every turn, split the same way as a single line's.
    /// Kept on the session rather than summed from the transcript so a compaction, which drops the
    /// history, cannot make the conversation look cheaper than it was.
    /// </summary>
    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int CacheTokens { get; set; }

    public int TotalTokens => InputTokens + OutputTokens + CacheTokens;

    /// <summary>Latest rolling summary, carried into the conversation after a compaction.</summary>
    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset? LastSummarisedAt { get; set; }
    public int SummaryCount { get; set; }

    /// <summary>Steers sent to this session, in the order they were given.</summary>
    public List<GuidanceMessage> Guidance { get; set; } = [];

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? LastError { get; set; }

    /// <summary>Set when the session was started by a workflow run, for tracing.</summary>
    public string? WorkflowRunId { get; set; }
    public string? ScheduleId { get; set; }

    /// <summary>Set when the session was started to process a queue item, for tracing.</summary>
    public string? QueueItemId { get; set; }

    /// <summary>
    /// MCP servers attached to this conversation specifically, on top of whatever the agent already
    /// has. Changing them applies from the next turn.
    /// </summary>
    public List<string> McpServerIds { get; set; } = [];

    /// <summary>Free-form operator notes, also readable and writable by agents over MCP.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Links the agent has hung on the sidebar over MCP — e.g. "Open PR" once it has opened one.
    /// The agent decides what goes here and how many; nothing here is capped.
    /// </summary>
    public List<SessionLink> Links { get; set; } = [];

    /// <summary>
    /// What this conversation is trying to achieve, in a sentence or two. Settable by the operator
    /// from the sidebar, by the agent over MCP, or derived automatically once there is enough
    /// conversation to summarise. Empty means no goal has been set yet.
    /// </summary>
    public string Goal { get; set; } = string.Empty;

    /// <summary>True when <see cref="Goal"/> was written by accepting an auto-derived suggestion
    /// rather than a person or the agent choosing its own words — shown in the UI so an operator
    /// knows it started as a guess.</summary>
    public bool GoalAutoDerived { get; set; }

    /// <summary>
    /// A guessed goal awaiting confirmation. Auto-derivation never writes <see cref="Goal"/>
    /// directly — a guess only starts being "the" goal, and gets acted on as one, once a person
    /// accepts or edits it from the sidebar. Cleared once accepted or dismissed.
    /// </summary>
    public string ProposedGoal { get; set; } = string.Empty;

    /// <summary>
    /// The turn count at which auto-derivation last ran (successfully or not), so a session that
    /// never produces a usable goal is not retried every single turn.
    /// </summary>
    public int LastGoalAttemptTurn { get; set; }

    /// <summary>Soft delete, so a session can be recovered from the archive.</summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// Keeps the age-based auto-archive off this session for good. Set when somebody restores it
    /// from the archive: pulling a conversation back out is a statement that it is still wanted, and
    /// a sweep that archived it again on the next tick would be arguing with the person who did it.
    /// </summary>
    public bool AutoArchiveExempt { get; set; }

    /// <summary>
    /// The conversation is finished and takes no further turns. Set when the work that owned the
    /// session is over — a queue item, whose agent answers to the queue rather than to anyone at the
    /// keyboard — so the transcript stays readable while nobody can restart a CLI behind the
    /// queue's back and leave the item's recorded outcome describing a run that has moved on.
    /// </summary>
    public bool IsClosed { get; set; }

    /// <summary>Why it was closed, shown in the chat where the composer used to be.</summary>
    public string? ClosedReason { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsLive => Status is SessionStatus.Starting or SessionStatus.Running or SessionStatus.AwaitingInput;

    /// <summary>
    /// A CLI is running right now. Narrower than <see cref="IsLive"/>, which also covers a session
    /// waiting on a person — that is the state an abandoned conversation sits in, so telling the
    /// two apart is what lets one be swept up without disturbing the other.
    /// </summary>
    public bool IsWorking => Status is SessionStatus.Starting or SessionStatus.Running;

    /// <summary>Whether another turn can be sent: a closed session is read only for good.</summary>
    public bool AcceptsInput => !IsClosed;

    /// <summary>Guidance the agent has not acted on yet.</summary>
    public IEnumerable<GuidanceMessage> PendingGuidance =>
        Guidance.Where(g => g.Status == GuidanceStatus.Pending);
}
