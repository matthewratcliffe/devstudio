using DevStudio.Domain.Common;

namespace DevStudio.Domain.Queues;

public enum QueueTarget
{
    Agent = 0,
    Workflow = 1,
}

/// <summary>What happens when an item arrives carrying a key the queue has seen before.</summary>
public enum QueueDedupe
{
    /// <summary>Nothing is turned away. The same key can be queued as often as it arrives.</summary>
    None = 0,

    /// <summary>
    /// Turned away while an item with that key is still waiting or running. This is what a poller
    /// wants: it re-reports the same open merge request every time it runs, and only the first
    /// report should become work.
    /// </summary>
    Active = 1,

    /// <summary>Turned away if that key has ever been queued, however long ago it finished.</summary>
    Ever = 2,
}

/// <summary>
/// A backlog of work items and the one thing that processes them. Something else fills it — a
/// scheduled agent that looks for open merge requests, an agent calling <c>enqueue</c> over MCP, or
/// a person adding a row by hand — and the dispatcher drains it by starting the queue's agent or
/// workflow once per item, up to <see cref="MaxConcurrent"/> at a time.
/// </summary>
public sealed class WorkQueue : Entity
{
    public string Name { get; set; } = "New queue";
    public string Description { get; set; } = string.Empty;

    public QueueTarget Target { get; set; } = QueueTarget.Agent;

    /// <summary>
    /// Id of the agent or workflow that processes an item. Optional: a queue with nothing set still
    /// accepts items and simply holds them, which is what lets a poller start collecting work before
    /// anybody has decided what should handle it. Nothing is dispatched until one is chosen.
    /// </summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Processes items inside this project's folder, with its instructions applied.</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Processes items on another machine, overriding whatever the agent is set to. Null follows the
    /// agent — which is what lets a queue's work be moved by repointing one agent.
    /// </summary>
    public string? RemoteInstanceId { get; set; }

    /// <summary>
    /// Prompt sent when the target is an agent. Supports {{item.title}}, {{item.body}}, {{item.key}}
    /// and every payload entry, both bare and as {{payload.name}}. Empty sends the item's title and
    /// body on their own.
    /// </summary>
    public string PromptTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Input values applied to every run when the target is a workflow. An item's payload is merged
    /// over the top, so a queue can carry the constants and the item only the part that varies.
    /// </summary>
    public Dictionary<string, string> Inputs { get; set; } = [];

    /// <summary>How many items of this queue may be in flight at once.</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>Total tries per item, including the first. Anything above one enables retries.</summary>
    public int MaxAttempts { get; set; } = 1;

    /// <summary>How long a failed item waits before it becomes available again.</summary>
    public int RetryDelayMinutes { get; set; } = 5;

    /// <summary>An item is abandoned and marked failed after this long.</summary>
    public int TimeoutMinutes { get; set; } = 60;

    public QueueDedupe Dedupe { get; set; } = QueueDedupe.Active;

    /// <summary>Items still arrive while this is false; nothing is dispatched until it is true again.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When set, dispatch pauses until this time because the provider reported a usage limit.</summary>
    public DateTimeOffset? SuspendedUntil { get; set; }

    public string? SuspensionReason { get; set; }
}
