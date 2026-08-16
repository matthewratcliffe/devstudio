using DevStudio.Application.Common;
using DevStudio.Application.Queues;
using DevStudio.Application.Sessions;
using DevStudio.Application.Workflows;
using DevStudio.Domain.Queues;
using DevStudio.Domain.Sessions;
using DevStudio.Domain.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Queues;

/// <summary>
/// Drains the queues. On each tick every enabled queue is offered as many items as it has free
/// slots, and each claimed item is processed on its own task — an agent session run to completion,
/// or a workflow run — with the outcome written back to the item.
///
/// The scheduler fires triggers on a clock; this fires them on a backlog. The two meet where a
/// schedule runs the bot that fills a queue.
/// </summary>
public sealed class QueueDispatcherHostedService : BackgroundService
{
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private readonly IQueueService _queues;
    private readonly ISessionManager _sessions;
    private readonly IWorkflowEngine _workflows;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<QueueDispatcherHostedService> _logger;

    public QueueDispatcherHostedService(
        IQueueService queues,
        ISessionManager sessions,
        IWorkflowEngine workflows,
        IOptions<OrchestratorOptions> options,
        ILogger<QueueDispatcherHostedService> logger)
    {
        _queues = queues;
        _sessions = sessions;
        _workflows = workflows;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Anything left running belonged to a process that is gone. Nothing else will release it.
        try
        {
            await _queues.ReleaseOrphansAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not release queue items left running by the last run");
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.QueueTickSeconds));
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation("Queue dispatcher started, ticking every {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queue dispatch tick failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                break;
        }
    }

    /// <summary>
    /// Dispatches whatever is due now, without waiting for the next tick. Called after something is
    /// enqueued from the UI so a queue with a free slot starts straight away.
    /// </summary>
    public Task PokeAsync(CancellationToken ct = default) => TickAsync(ct);

    private async Task TickAsync(CancellationToken ct)
    {
        // Ticks must not overlap: two of them would each see the same free slots and both claim.
        if (!await _tickLock.WaitAsync(0, ct))
            return;

        try
        {
            foreach (var queue in await _queues.GetQueuesAsync(ct))
            {
                if (!queue.Enabled || string.IsNullOrWhiteSpace(queue.TargetId))
                    continue;

                var counts = await _queues.GetCountsAsync(queue.Id, ct);
                var slots = Math.Max(1, queue.MaxConcurrent) - counts.Running;
                if (slots <= 0)
                    continue;

                var claimed = await _queues.ClaimAsync(queue.Id, slots, ct);

                // Deliberately not awaited: a queue's items run for as long as the agent takes, and
                // the tick has other queues to offer work to.
                foreach (var item in claimed)
                    _ = ProcessAsync(queue, item);
            }
        }
        finally
        {
            _tickLock.Release();
        }
    }

    private async Task ProcessAsync(WorkQueue queue, QueueItem item)
    {
        try
        {
            var outcome = queue.Target == QueueTarget.Workflow
                ? await RunWorkflowAsync(queue, item)
                : await RunAgentAsync(queue, item);

            await _queues.CompleteAsync(item, outcome, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queue item {ItemId} on {Queue} threw", item.Id, queue.Name);

            try
            {
                await _queues.CompleteAsync(item, QueueOutcome.Failure(ex.Message), CancellationToken.None);
            }
            catch (Exception persistFailure)
            {
                // Losing the outcome would leave the item running forever; the restart sweep is the
                // only thing that would ever free it, so say so loudly.
                _logger.LogError(persistFailure, "Could not record the outcome of queue item {ItemId}", item.Id);
            }
        }
    }

    private async Task<QueueOutcome> RunAgentAsync(WorkQueue queue, QueueItem item)
    {
        var session = await _sessions.RunToCompletionAsync(
            new StartSessionRequest
            {
                AgentId = queue.TargetId,
                Prompt = QueueContext.RenderPrompt(queue, item),
                Title = $"{queue.Name} · {Summarise(item)}",
                Trigger = SessionTrigger.Queue,
                ProjectId = queue.ProjectId,
                QueueItemId = item.Id,
            },
            TimeSpan.FromMinutes(Math.Max(1, queue.TimeoutMinutes)));

        var output = session.Messages
            .Where(m => m.Role == MessageRole.Agent && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content)
            .LastOrDefault() ?? string.Empty;

        // The item is what this conversation was for, and it is over: close the session so the
        // transcript stays readable but nobody can send it another turn. Whatever the agent said
        // last is about to be recorded against the item, and a later turn would make that a lie.
        try
        {
            await _sessions.CloseAsync(
                session.Id,
                $"Finished the queue item on {queue.Name}.",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The item's outcome matters more than the session being sealed, so this is not fatal.
            _logger.LogWarning(ex, "Could not close session {SessionId} after its queue item finished", session.Id);
        }

        if (session.Status is SessionStatus.Failed or SessionStatus.Cancelled)
        {
            var error = session.LastError ?? "The agent did not finish.";
            return UsageLimitBackoff.TryGetUntil(error, DateTimeOffset.UtcNow, out var until)
                ? QueueOutcome.Failure(error, session.Id) with { Output = output, SuspendUntil = until }
                : QueueOutcome.Failure(error, session.Id) with { Output = output };
        }

        return QueueOutcome.Success(output, session.Id);
    }

    private async Task<QueueOutcome> RunWorkflowAsync(WorkQueue queue, QueueItem item)
    {
        var run = await _workflows.RunAsync(
            queue.TargetId,
            QueueContext.BuildInputs(queue, item),
            $"queue:{queue.Name}");

        var output = run.Steps.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.Output))?.Output;

        return run.Status == RunStatus.Succeeded
            ? QueueOutcome.Success(output, runId: run.Id)
            : QueueOutcome.Failure(run.Error ?? $"The run ended {run.Status}.", runId: run.Id) with { Output = output };
    }

    /// <summary>A session title has to fit on a line, and an item's title can be a whole sentence.</summary>
    private static string Summarise(QueueItem item)
    {
        var text = string.IsNullOrWhiteSpace(item.Title) ? item.Key : item.Title;
        text = text.ReplaceLineEndings(" ").Trim();

        return text.Length <= 60 ? text : $"{text[..57]}...";
    }
}
