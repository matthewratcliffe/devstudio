using DevStudio.Domain.Common;

namespace DevStudio.Domain.Scheduling;

public enum ScheduleTarget
{
    Agent = 0,
    Workflow = 1,
}

public enum ScheduleKind
{
    /// <summary>Fires on a five-field cron expression.</summary>
    Cron = 0,
    /// <summary>Fires every N minutes from the moment it was enabled.</summary>
    Interval = 1,
    /// <summary>Never fires by itself; kept so a saved run configuration can be triggered by hand.</summary>
    Manual = 2,
}

/// <summary>A cron-driven trigger that starts an agent session or a workflow run.</summary>
public sealed class Schedule : Entity
{
    public string Name { get; set; } = "New schedule";
    public string Description { get; set; } = string.Empty;
    public ScheduleKind Kind { get; set; } = ScheduleKind.Cron;

    /// <summary>Standard five-field cron: minute hour day-of-month month day-of-week.</summary>
    public string CronExpression { get; set; } = "0 * * * *";

    /// <summary>Used when <see cref="Kind"/> is <see cref="ScheduleKind.Interval"/>.</summary>
    public int IntervalMinutes { get; set; } = 60;
    /// <summary>Time zone id the cron fields are evaluated in.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    public ScheduleTarget Target { get; set; } = ScheduleTarget.Agent;
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Runs the target inside this project's folder, with its instructions applied.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Prompt sent when the target is an agent.</summary>
    public string Prompt { get; set; } = string.Empty;
    /// <summary>Input values when the target is a workflow.</summary>
    public Dictionary<string, string> Inputs { get; set; } = [];

    public bool Enabled { get; set; } = true;
    /// <summary>Skip a firing while the previous run of this schedule is still going.</summary>
    public bool SkipIfRunning { get; set; } = true;

    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string? LastRunId { get; set; }
    public string? LastError { get; set; }
    public int RunCount { get; set; }
}
