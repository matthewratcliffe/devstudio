using DevStudio.Application.Agents;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Sessions;

namespace DevStudio.Application.Sessions;

public sealed record StartSessionRequest
{
    public required string AgentId { get; init; }
    public required string Prompt { get; init; }
    public string? Title { get; init; }

    /// <summary>Overrides the agent's own project, so one agent can serve several projects.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Extra MCP servers for this conversation, beyond the agent's own.</summary>
    public IReadOnlyList<string> McpServerIds { get; init; } = [];

    /// <summary>
    /// Overrides the agent's permission mode for this session. Needed because Plan mode blocks every
    /// MCP tool call outright, so a chat that is meant to use tools cannot run in it.
    /// </summary>
    public PermissionMode? PermissionMode { get; init; }

    /// <summary>
    /// Model settings for this conversation only, overriding the agent's — including a handover from
    /// an opening model to a cheaper one. Null leaves the agent in charge of all of it.
    /// </summary>
    public SessionModelSettings? Model { get; init; }

    public SessionTrigger Trigger { get; init; } = SessionTrigger.Manual;
    public string? WorkflowRunId { get; init; }
    public string? ScheduleId { get; init; }

    /// <summary>Set when the session is processing a queue item, for tracing.</summary>
    public string? QueueItemId { get; init; }

    /// <summary>Run in this directory instead of provisioning a fresh workspace.</summary>
    public string? WorkingDirectoryOverride { get; init; }
}

/// <summary>
/// Owns every running conversation. Turns are queued per session and executed concurrently across
/// sessions, up to the configured cap.
/// </summary>
public interface ISessionManager
{
    /// <summary>Sessions with a live process or a queued turn, newest first.</summary>
    IReadOnlyList<ChatSession> Live { get; }

    /// <summary>Fired on every transcript or status change, for UI refresh.</summary>
    event Action<ChatSession>? SessionUpdated;

    Task<ChatSession> StartAsync(StartSessionRequest request, CancellationToken ct = default);

    /// <summary>Queues another user turn on an existing session.</summary>
    Task SendAsync(string sessionId, string message, CancellationToken ct = default);

    /// <summary>
    /// Steers a session that is already working. The guidance is published to the workspace and to
    /// MCP straight away, and folded into the next turn regardless. With
    /// <paramref name="interrupt"/> the turn in flight is stopped so the steer takes effect now.
    /// </summary>
    Task<GuidanceMessage> SendGuidanceAsync(
        string sessionId,
        string guidance,
        string source = "operator",
        bool interrupt = false,
        CancellationToken ct = default);

    /// <summary>
    /// Hands a running agent the guidance waiting for it and marks it delivered. This is what the
    /// orchestrator's own MCP server calls, and the only way a steer reaches an agent mid-turn.
    /// </summary>
    Task<IReadOnlyList<GuidanceMessage>> TakeGuidanceAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Answers a permission request the CLI parked because it had nobody to ask. Returns null when
    /// the session or the request has gone.
    /// </summary>
    Task<ToolApproval?> ResolveApprovalAsync(
        string sessionId,
        string approvalId,
        bool allow,
        CancellationToken ct = default);

    Task CancelAsync(string sessionId);

    /// <summary>Loads a session from memory when live, otherwise from the store.</summary>
    Task<ChatSession?> GetAsync(string sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<ChatSession>> GetAllAsync(CancellationToken ct = default);

    Task<bool> DeleteAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Starts a session and waits for it to go idle. Used by workflow steps.</summary>
    Task<ChatSession> RunToCompletionAsync(
        StartSessionRequest request,
        TimeSpan timeout,
        CancellationToken ct = default);
}
