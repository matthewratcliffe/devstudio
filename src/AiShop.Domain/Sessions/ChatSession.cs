using AiShop.Domain.Agents;
using AiShop.Domain.Common;
using AiShop.Domain.Providers;

namespace AiShop.Domain.Sessions;

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
}

public enum SessionTrigger
{
    Manual = 0,
    Schedule = 1,
    Workflow = 2,
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

/// <summary>A single conversation with one CLI process. Many run concurrently.</summary>
public sealed class ChatSession : Entity
{
    public string Title { get; set; } = "Untitled session";
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public AiProvider Provider { get; set; }

    /// <summary>Set for a user-defined CLI, with its name kept for display.</summary>
    public string? CliProviderId { get; set; }
    public string? CliProviderName { get; set; }
    public PermissionMode PermissionMode { get; set; }
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

    /// <summary>Turns completed, which is what the project's summarisation threshold counts.</summary>
    public int TurnCount { get; set; }

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

    /// <summary>
    /// MCP servers attached to this conversation specifically, on top of whatever the agent already
    /// has. Changing them applies from the next turn.
    /// </summary>
    public List<string> McpServerIds { get; set; } = [];

    /// <summary>Free-form operator notes, also readable and writable by agents over MCP.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Soft delete, so a session can be recovered from the archive.</summary>
    public bool IsArchived { get; set; }

    public bool IsLive => Status is SessionStatus.Starting or SessionStatus.Running or SessionStatus.AwaitingInput;

    /// <summary>Guidance the agent has not acted on yet.</summary>
    public IEnumerable<GuidanceMessage> PendingGuidance =>
        Guidance.Where(g => g.Status == GuidanceStatus.Pending);
}
