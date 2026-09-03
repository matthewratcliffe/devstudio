using DevStudio.Domain.Common;

namespace DevStudio.Domain.Workflows;

public enum RunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

public sealed class WorkflowRun : Entity
{
    public string WorkflowId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string? ProjectId { get; set; }

    /// <summary>The machine every step of this run executes on, captured when it started.</summary>
    public string? RemoteInstanceId { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Pending;
    public Dictionary<string, string> Inputs { get; set; } = [];
    public List<WorkflowStepRun> Steps { get; set; } = [];
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public string? TriggeredBy { get; set; }
}

public sealed class WorkflowStepRun
{
    public string StepId { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public int Order { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public string? SessionId { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
