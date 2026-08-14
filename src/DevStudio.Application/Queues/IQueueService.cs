using DevStudio.Domain.Queues;

namespace DevStudio.Application.Queues;

/// <summary>An item offered to a queue. Everything but the queue itself is optional.</summary>
public sealed record EnqueueRequest
{
    public required string QueueId { get; init; }

    /// <summary>
    /// The producer's identity for this work. Deduplication happens on it, so a poller that keeps
    /// finding the same merge request open should send the same key every time. Empty disables
    /// deduplication for this item.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string>? Payload { get; init; }

    /// <summary>Higher goes first.</summary>
    public int Priority { get; init; }

    public string EnqueuedBy { get; init; } = "manual";
}

/// <summary>
/// The outcome of an offer. A rejection is a normal answer, not an error: a poller re-reporting work
/// that is already queued is the expected case, and it should read as "already have it".
/// </summary>
public sealed record EnqueueResult(bool Accepted, QueueItem? Item, string Reason)
{
    public static EnqueueResult Added(QueueItem item) => new(true, item, "Queued.");

    public static EnqueueResult Duplicate(QueueItem existing) =>
        new(false, existing, $"Already queued as {existing.Id} ({existing.Status}).");
}

/// <summary>Item counts for one queue, for the list and the badges.</summary>
public sealed record QueueCounts(int Pending, int Running, int Succeeded, int Failed, int Cancelled)
{
    public int Total => Pending + Running + Succeeded + Failed + Cancelled;

    /// <summary>What is left to do, which is the number worth showing next to the queue's name.</summary>
    public int Outstanding => Pending + Running;
}

/// <summary>
/// Owns the backlogs. Producers call <see cref="EnqueueAsync"/>; the dispatcher calls
/// <see cref="ClaimAsync"/> and <see cref="CompleteAsync"/>. Every write goes through here so that
/// claiming is serialised — two dispatch ticks must never hand the same item to two agents.
/// </summary>
public interface IQueueService
{
    Task<IReadOnlyList<WorkQueue>> GetQueuesAsync(CancellationToken ct = default);

    Task<WorkQueue?> GetQueueAsync(string queueId, CancellationToken ct = default);

    /// <summary>
    /// Finds a queue by id or by name. <see cref="EnqueueAsync"/> resolves the same way, so a caller
    /// that only knows what the queue is called does not have to look the id up first.
    /// </summary>
    Task<WorkQueue?> ResolveQueueAsync(string idOrName, CancellationToken ct = default);

    /// <summary>Offers an item, honouring the queue's deduplication rule.</summary>
    Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default);

    /// <summary>Items on one queue, or every item when <paramref name="queueId"/> is null. Newest first.</summary>
    Task<IReadOnlyList<QueueItem>> GetItemsAsync(string? queueId = null, CancellationToken ct = default);

    Task<QueueItem?> GetItemAsync(string itemId, CancellationToken ct = default);

    Task<QueueCounts> GetCountsAsync(string queueId, CancellationToken ct = default);

    /// <summary>
    /// Takes up to <paramref name="max"/> due items and marks them running. Serialised against every
    /// other caller, so an item is only ever claimed once.
    /// </summary>
    Task<IReadOnlyList<QueueItem>> ClaimAsync(string queueId, int max, CancellationToken ct = default);

    /// <summary>
    /// Records what became of a claimed item. A failure with tries left goes back to pending behind
    /// the queue's retry delay; otherwise it settles as failed.
    /// </summary>
    Task CompleteAsync(QueueItem item, QueueOutcome outcome, CancellationToken ct = default);

    /// <summary>Puts a finished item back on the queue as pending, with its attempt count reset.</summary>
    Task<bool> RetryAsync(string itemId, CancellationToken ct = default);

    /// <summary>Cancels a pending item, or a running one along with the work processing it.</summary>
    Task<bool> CancelAsync(string itemId, CancellationToken ct = default);

    /// <summary>Deletes items. Finished ones only unless <paramref name="everything"/> is set.</summary>
    Task<int> PurgeAsync(string queueId, bool everything = false, CancellationToken ct = default);

    Task<bool> DeleteItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Puts every running item back to pending. Called on start: a process that died mid-run left
    /// items claimed by an agent that no longer exists, and nothing else will ever release them.
    /// </summary>
    Task<int> ReleaseOrphansAsync(CancellationToken ct = default);

    /// <summary>Raised on any item change so the UI can refresh without polling.</summary>
    event Action<QueueItem>? ItemChanged;
}

/// <summary>How processing an item ended.</summary>
public sealed record QueueOutcome
{
    public bool Failed { get; init; }
    public string? Error { get; init; }
    public string? Output { get; init; }
    public string? SessionId { get; init; }
    public string? WorkflowRunId { get; init; }

    public static QueueOutcome Success(string? output, string? sessionId = null, string? runId = null) =>
        new() { Output = output, SessionId = sessionId, WorkflowRunId = runId };

    public static QueueOutcome Failure(string error, string? sessionId = null, string? runId = null) =>
        new() { Failed = true, Error = error, SessionId = sessionId, WorkflowRunId = runId };
}
