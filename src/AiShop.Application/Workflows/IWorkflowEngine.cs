using AiShop.Domain.Workflows;

namespace AiShop.Application.Workflows;

/// <summary>
/// Runs a workflow's steps in order. Every finished step publishes its output into a run-wide
/// context, so any later step can reference it as {{steps.step-name}} regardless of how far back
/// it ran; {{previous}} is a shortcut for the step immediately before.
/// </summary>
public interface IWorkflowEngine
{
    IReadOnlyList<WorkflowRun> Active { get; }

    event Action<WorkflowRun>? RunUpdated;

    /// <summary>Starts a run and returns once it finishes.</summary>
    Task<WorkflowRun> RunAsync(
        string workflowId,
        IReadOnlyDictionary<string, string> inputs,
        string triggeredBy,
        CancellationToken ct = default);

    /// <summary>Starts a run in the background and returns immediately with the created run.</summary>
    Task<WorkflowRun> StartAsync(
        string workflowId,
        IReadOnlyDictionary<string, string> inputs,
        string triggeredBy,
        CancellationToken ct = default);

    Task CancelAsync(string runId);

    Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(string? workflowId = null, CancellationToken ct = default);

    Task<WorkflowRun?> GetRunAsync(string runId, CancellationToken ct = default);
}
