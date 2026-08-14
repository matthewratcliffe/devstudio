using DevStudio.Application.Abstractions;
using DevStudio.Application.Sessions;
using DevStudio.Application.Workflows;
using DevStudio.Domain.Queues;
using Microsoft.Extensions.Logging;

namespace DevStudio.Application.Queues;

/// <summary>
/// Default implementation over the JSON stores. Claiming is guarded by a single lock because the
/// store has no querying and no transactions: read-modify-write is the only shape available, so the
/// only way two dispatch ticks cannot claim the same item is to let one of them run at a time.
/// </summary>
public sealed class QueueService : IQueueService
{
    private readonly SemaphoreSlim _claimLock = new(1, 1);
    private readonly IEntityStore<WorkQueue> _queues;
    private readonly IEntityStore<QueueItem> _items;
    private readonly ISessionManager _sessions;
    private readonly IWorkflowEngine _workflows;
    private readonly ILogger<QueueService> _logger;

    public QueueService(
        IEntityStore<WorkQueue> queues,
        IEntityStore<QueueItem> items,
        ISessionManager sessions,
        IWorkflowEngine workflows,
        ILogger<QueueService> logger)
    {
        _queues = queues;
        _items = items;
        _sessions = sessions;
        _workflows = workflows;
        _logger = logger;
    }

    public event Action<QueueItem>? ItemChanged;

    public Task<IReadOnlyList<WorkQueue>> GetQueuesAsync(CancellationToken ct = default) =>
        _queues.GetAllAsync(ct);

    public Task<WorkQueue?> GetQueueAsync(string queueId, CancellationToken ct = default) =>
        _queues.GetAsync(queueId, ct);

    /// <summary>
    /// Finds a queue by its id or by its name. Producers are told the id, but an agent asked to "add
    /// this to the code review queue" has only the name to go on, so a name has to work as well as an
    /// id — otherwise the first attempt fails and only a second, better-informed one succeeds.
    /// </summary>
    public async Task<WorkQueue?> ResolveQueueAsync(string idOrName, CancellationToken ct = default)
    {
        var wanted = idOrName?.Trim() ?? string.Empty;
        if (wanted.Length == 0)
            return null;

        var all = await _queues.GetAllAsync(ct);

        var exact = all.FirstOrDefault(q => string.Equals(q.Id, wanted, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(q => string.Equals(q.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
            return exact;

        // Beyond an exact match, only an unambiguous one counts: guessing between two queues would
        // file the work somewhere nobody is watching, which is worse than saying the name was unclear.
        var slug = Slug(wanted);
        if (slug.Length == 0)
            return null;

        return Only(all.Where(q => Slug(q.Name) == slug))
               ?? Only(all.Where(q => Slug(q.Name).Contains(slug, StringComparison.Ordinal)));

        static WorkQueue? Only(IEnumerable<WorkQueue> matches)
        {
            var found = matches.Take(2).ToList();
            return found.Count == 1 ? found[0] : null;
        }
    }

    /// <summary>
    /// Names what is actually there. A caller that guessed the name wrong can only correct itself if
    /// the refusal says what the choices were.
    /// </summary>
    private async Task<string> UnknownQueueMessageAsync(string wanted, CancellationToken ct)
    {
        var names = (await _queues.GetAllAsync(ct)).Select(q => $"'{q.Name}'").ToList();

        return names.Count == 0
            ? $"There is no queue '{wanted}'. No queues are configured."
            : $"There is no queue '{wanted}'. The queues are: {string.Join(", ", names)}.";
    }

    /// <summary>Names compared with their punctuation and spacing dropped, so "Code Review" finds "code-review".</summary>
    private static string Slug(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public async Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default)
    {
        var queue = await ResolveQueueAsync(request.QueueId, ct)
                    ?? throw new InvalidOperationException(await UnknownQueueMessageAsync(request.QueueId, ct));

        var key = request.Key.Trim();

        // Held across the whole check-then-add: two pollers finding the same merge request at the
        // same moment must not both get past the duplicate check.
        await _claimLock.WaitAsync(ct);
        try
        {
            if (key.Length > 0 && queue.Dedupe != QueueDedupe.None)
            {
                var existing = (await _items.GetAllAsync(ct))
                    .Where(i => i.QueueId == queue.Id && string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))
                    .Where(i => queue.Dedupe == QueueDedupe.Ever || !i.IsFinished)
                    .OrderByDescending(i => i.CreatedAt)
                    .FirstOrDefault();

                if (existing is not null)
                    return EnqueueResult.Duplicate(existing);
            }

            var item = new QueueItem
            {
                QueueId = queue.Id,
                Key = key,
                Title = string.IsNullOrWhiteSpace(request.Title) ? key : request.Title.Trim(),
                Body = request.Body,
                Payload = request.Payload is null ? [] : new Dictionary<string, string>(request.Payload),
                Priority = request.Priority,
                EnqueuedBy = request.EnqueuedBy,
            };

            await _items.UpsertAsync(item, ct);
            ItemChanged?.Invoke(item);
            return EnqueueResult.Added(item);
        }
        finally
        {
            _claimLock.Release();
        }
    }

    public async Task<IReadOnlyList<QueueItem>> GetItemsAsync(string? queueId = null, CancellationToken ct = default)
    {
        var all = await _items.GetAllAsync(ct);
        return all
            .Where(i => queueId is null || i.QueueId == queueId)
            .OrderByDescending(i => i.CreatedAt)
            .ToList();
    }

    public Task<QueueItem?> GetItemAsync(string itemId, CancellationToken ct = default) =>
        _items.GetAsync(itemId, ct);

    public async Task<QueueCounts> GetCountsAsync(string queueId, CancellationToken ct = default)
    {
        var items = (await _items.GetAllAsync(ct)).Where(i => i.QueueId == queueId).ToList();

        return new QueueCounts(
            items.Count(i => i.Status == QueueItemStatus.Pending),
            items.Count(i => i.Status == QueueItemStatus.Running),
            items.Count(i => i.Status == QueueItemStatus.Succeeded),
            items.Count(i => i.Status == QueueItemStatus.Failed),
            items.Count(i => i.Status == QueueItemStatus.Cancelled));
    }

    public async Task<IReadOnlyList<QueueItem>> ClaimAsync(string queueId, int max, CancellationToken ct = default)
    {
        if (max <= 0)
            return [];

        await _claimLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;

            var due = (await _items.GetAllAsync(ct))
                .Where(i => i.QueueId == queueId && i.Status == QueueItemStatus.Pending)
                .Where(i => i.AvailableAt is null || i.AvailableAt <= now)
                .OrderByDescending(i => i.Priority)
                .ThenBy(i => i.CreatedAt)
                .Take(max)
                .ToList();

            foreach (var item in due)
            {
                item.Status = QueueItemStatus.Running;
                item.Attempts++;
                item.StartedAt = now;
                item.FinishedAt = null;
                item.AvailableAt = null;
                item.LastError = null;
                await _items.UpsertAsync(item, ct);
                ItemChanged?.Invoke(item);
            }

            return due;
        }
        finally
        {
            _claimLock.Release();
        }
    }

    public async Task CompleteAsync(QueueItem item, QueueOutcome outcome, CancellationToken ct = default)
    {
        // Reread: the item has been in flight for however long the agent took, and the operator may
        // have cancelled or deleted it in the meantime. Writing the in-memory copy back would undo
        // a cancellation, or resurrect a row that emptying the queue removed.
        var stored = await _items.GetAsync(item.Id, ct);
        if (stored is null || stored.Status == QueueItemStatus.Cancelled)
            return;

        var queue = await _queues.GetAsync(stored.QueueId, ct);

        stored.SessionId = outcome.SessionId ?? stored.SessionId;
        stored.WorkflowRunId = outcome.WorkflowRunId ?? stored.WorkflowRunId;
        stored.Output = outcome.Output ?? stored.Output;
        stored.LastError = outcome.Error;

        var attemptsAllowed = Math.Max(1, queue?.MaxAttempts ?? 1);

        if (outcome.Failed && stored.Attempts < attemptsAllowed)
        {
            var delay = Math.Max(0, queue?.RetryDelayMinutes ?? 0);
            stored.Status = QueueItemStatus.Pending;
            stored.AvailableAt = DateTimeOffset.UtcNow.AddMinutes(delay);
            stored.FinishedAt = null;

            _logger.LogInformation(
                "Queue item {ItemId} failed on attempt {Attempt} of {Allowed}; retrying in {Delay}m",
                stored.Id, stored.Attempts, attemptsAllowed, delay);
        }
        else
        {
            stored.Status = outcome.Failed ? QueueItemStatus.Failed : QueueItemStatus.Succeeded;
            stored.FinishedAt = DateTimeOffset.UtcNow;
        }

        await _items.UpsertAsync(stored, ct);
        ItemChanged?.Invoke(stored);
    }

    public async Task<bool> RetryAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _items.GetAsync(itemId, ct);
        if (item is null || item.Status == QueueItemStatus.Running)
            return false;

        item.Status = QueueItemStatus.Pending;
        item.Attempts = 0;
        item.AvailableAt = null;
        item.StartedAt = null;
        item.FinishedAt = null;
        item.LastError = null;

        await _items.UpsertAsync(item, ct);
        ItemChanged?.Invoke(item);
        return true;
    }

    public async Task<bool> CancelAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _items.GetAsync(itemId, ct);
        if (item is null || item.IsFinished)
            return false;

        // Stop the work first: marking the item cancelled is what stops a late CompleteAsync
        // overwriting this, and the session or run would otherwise carry on with nobody waiting.
        if (item.Status == QueueItemStatus.Running)
        {
            try
            {
                if (item.SessionId is { Length: > 0 } sessionId)
                    await _sessions.CancelAsync(sessionId);

                if (item.WorkflowRunId is { Length: > 0 } runId)
                    await _workflows.CancelAsync(runId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not stop the work processing queue item {ItemId}", itemId);
            }
        }

        item.Status = QueueItemStatus.Cancelled;
        item.FinishedAt = DateTimeOffset.UtcNow;
        await _items.UpsertAsync(item, ct);
        ItemChanged?.Invoke(item);
        return true;
    }

    public async Task<int> PurgeAsync(string queueId, bool everything = false, CancellationToken ct = default)
    {
        var doomed = (await _items.GetAllAsync(ct))
            .Where(i => i.QueueId == queueId)
            .Where(i => everything || i.IsFinished)
            .ToList();

        foreach (var item in doomed)
        {
            if (item.Status == QueueItemStatus.Running)
                await CancelAsync(item.Id, ct);

            await _items.DeleteAsync(item.Id, ct);
        }

        return doomed.Count;
    }

    public async Task<bool> DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _items.GetAsync(itemId, ct);
        if (item is not null && item.Status == QueueItemStatus.Running)
            await CancelAsync(itemId, ct);

        return await _items.DeleteAsync(itemId, ct);
    }

    public async Task<int> ReleaseOrphansAsync(CancellationToken ct = default)
    {
        var stranded = (await _items.GetAllAsync(ct))
            .Where(i => i.Status == QueueItemStatus.Running)
            .ToList();

        foreach (var item in stranded)
        {
            item.Status = QueueItemStatus.Pending;
            item.StartedAt = null;
            item.LastError = "The orchestrator restarted while this item was being processed.";
            // The interrupted try still counts against MaxAttempts, so an item that kills the
            // process cannot loop forever across restarts.
            await _items.UpsertAsync(item, ct);
            ItemChanged?.Invoke(item);
        }

        if (stranded.Count > 0)
            _logger.LogInformation("Released {Count} queue item(s) left running by a restart", stranded.Count);

        return stranded.Count;
    }
}
