using System.Collections.Concurrent;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Application.Scheduling;
using DevStudio.Application.Sessions;
using DevStudio.Application.Workflows;
using DevStudio.Domain.Scheduling;
using DevStudio.Domain.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Scheduling;

/// <summary>
/// Wakes on a short tick, works out which schedules are due, and triggers them. Firing times are
/// stored on the schedule so a restart does not lose or double-fire a slot.
/// </summary>
public sealed class SchedulerHostedService : BackgroundService
{
    private readonly ConcurrentDictionary<string, byte> _running = new();
    private readonly IEntityStore<Schedule> _schedules;
    private readonly ISessionManager _sessions;
    private readonly IWorkflowEngine _workflows;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SchedulerHostedService> _logger;

    public SchedulerHostedService(
        IEntityStore<Schedule> schedules,
        ISessionManager sessions,
        IWorkflowEngine workflows,
        IOptions<OrchestratorOptions> options,
        ILogger<SchedulerHostedService> logger)
    {
        _schedules = schedules;
        _sessions = sessions;
        _workflows = workflows;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.SchedulerTickSeconds));
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation("Scheduler started, ticking every {Interval}", interval);

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
                _logger.LogError(ex, "Scheduler tick failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                break;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var schedule in await _schedules.GetAllAsync(ct))
        {
            if (!schedule.Enabled || schedule.Kind == ScheduleKind.Manual)
                continue;

            var next = schedule.NextRunAt ?? ComputeNext(schedule, schedule.LastRunAt ?? now);
            if (next is null)
                continue;

            if (schedule.NextRunAt is null)
            {
                schedule.NextRunAt = next;
                await _schedules.UpsertAsync(schedule, ct);
                continue;
            }

            if (next > now)
                continue;

            if (schedule.SkipIfRunning && IsPreviousRunStillGoing(schedule))
            {
                _logger.LogInformation("Schedule {Name} is still running; skipping this slot", schedule.Name);
                schedule.NextRunAt = ComputeNext(schedule, now);
                await _schedules.UpsertAsync(schedule, ct);
                continue;
            }

            schedule.LastRunAt = now;
            schedule.NextRunAt = ComputeNext(schedule, now);
            schedule.RunCount++;
            await _schedules.UpsertAsync(schedule, ct);

            _ = FireAsync(schedule);
        }
    }

    private bool IsPreviousRunStillGoing(Schedule schedule) =>
        _running.ContainsKey(schedule.Id) ||
        _sessions.Live.Any(session =>
            session.ScheduleId == schedule.Id && session.IsWorking);

    /// <summary>Runs a schedule immediately, outside its normal timing. Used by the "Run now" button.</summary>
    public async Task TriggerAsync(Schedule schedule)
    {
        schedule.LastRunAt = DateTimeOffset.UtcNow;
        schedule.RunCount++;
        await _schedules.UpsertAsync(schedule);
        await FireAsync(schedule);
    }

    private async Task FireAsync(Schedule schedule)
    {
        _running[schedule.Id] = 0;

        try
        {
            if (schedule.Target == ScheduleTarget.Workflow)
            {
                var inputs = new Dictionary<string, string>(schedule.Inputs);
                if (!string.IsNullOrWhiteSpace(schedule.ProjectId))
                    inputs["projectId"] = schedule.ProjectId!;

                var run = await _workflows.RunAsync(schedule.TargetId, inputs, $"schedule:{schedule.Name}");
                schedule.LastRunId = run.Id;
                schedule.LastError = run.Error;
            }
            else
            {
                var session = await _sessions.StartAsync(new StartSessionRequest
                {
                    AgentId = schedule.TargetId,
                    Prompt = schedule.Prompt,
                    Title = $"{schedule.Name} · {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}",
                    Trigger = SessionTrigger.Schedule,
                    ScheduleId = schedule.Id,
                    ProjectId = schedule.ProjectId,
                    RemoteInstanceId = schedule.RemoteInstanceId,
                });

                schedule.LastRunId = session.Id;
                schedule.LastError = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schedule {Name} failed to start", schedule.Name);
            schedule.LastError = ex.Message;
        }
        finally
        {
            _running.TryRemove(schedule.Id, out _);
            await _schedules.UpsertAsync(schedule, CancellationToken.None);
        }
    }

    /// <summary>Next firing after <paramref name="from"/>, or null when the schedule cannot be parsed.</summary>
    public static DateTimeOffset? ComputeNext(Schedule schedule, DateTimeOffset from)
    {
        switch (schedule.Kind)
        {
            case ScheduleKind.Interval:
                var minutes = Math.Max(1, schedule.IntervalMinutes);
                return from.AddMinutes(minutes);

            case ScheduleKind.Cron:
                if (!CronExpression.TryParse(schedule.CronExpression, out var cron, out _) || cron is null)
                    return null;

                return cron.GetNextOccurrence(from, ResolveTimeZone(schedule.TimeZoneId));

            default:
                return null;
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            // An unknown zone must not stop the schedule from running at all.
            return TimeZoneInfo.Utc;
        }
    }
}
