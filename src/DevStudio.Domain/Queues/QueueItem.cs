using DevStudio.Domain.Common;

namespace DevStudio.Domain.Queues;

public enum QueueItemStatus
{
    /// <summary>Waiting for a slot. Also where a failed item goes back to while retries remain.</summary>
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>One unit of work on a queue: an open merge request, a ticket, a file to process.</summary>
public sealed class QueueItem : Entity
{
    public string QueueId { get; set; } = string.Empty;

    /// <summary>
    /// The item's identity to whatever produced it — a merge request URL, an issue key. Deduplication
    /// is done on this, so a poller can re-report the same thing every run without piling up work.
    /// Empty means the item is always accepted.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>One line describing the item, shown in the list.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The detail — a description, a diff summary — folded into the prompt.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Anything else the producer knows, readable in the prompt template and passed to a workflow as
    /// inputs.
    /// </summary>
    public Dictionary<string, string> Payload { get; set; } = [];

    /// <summary>Higher goes first. Items of equal priority are taken oldest first.</summary>
    public int Priority { get; set; }

    public QueueItemStatus Status { get; set; } = QueueItemStatus.Pending;

    /// <summary>Tries so far, including the one in flight.</summary>
    public int Attempts { get; set; }

    /// <summary>Not dispatched before this. Set when a failed item is waiting out its retry delay.</summary>
    public DateTimeOffset? AvailableAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Session that processed it, when the queue targets an agent.</summary>
    public string? SessionId { get; set; }

    /// <summary>Run that processed it, when the queue targets a workflow.</summary>
    public string? WorkflowRunId { get; set; }

    /// <summary>The agent's last message, kept so the list can show what came of the item.</summary>
    public string? Output { get; set; }

    public string? LastError { get; set; }

    /// <summary>Who put it here: "manual", "mcp", "agent:&lt;name&gt;".</summary>
    public string EnqueuedBy { get; set; } = "manual";

    public bool IsFinished => Status is QueueItemStatus.Succeeded or QueueItemStatus.Failed or QueueItemStatus.Cancelled;
}
