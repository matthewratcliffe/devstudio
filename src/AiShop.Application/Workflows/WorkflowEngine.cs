using System.Collections.Concurrent;
using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Application.Sessions;
using AiShop.Domain.Sessions;
using AiShop.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace AiShop.Application.Workflows;

/// <summary>
/// Default engine. Steps are grouped by <see cref="WorkflowStep.Order"/>: everything sharing an
/// order runs concurrently, and the next order starts once that whole group has settled.
/// </summary>
public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly ConcurrentDictionary<string, (WorkflowRun Run, CancellationTokenSource Cts)> _active = new();
    private readonly IEntityStore<Workflow> _workflows;
    private readonly IEntityStore<WorkflowRun> _runs;
    private readonly ISessionManager _sessions;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IEntityStore<Workflow> workflows,
        IEntityStore<WorkflowRun> runs,
        ISessionManager sessions,
        ILogger<WorkflowEngine> logger)
    {
        _workflows = workflows;
        _runs = runs;
        _sessions = sessions;
        _logger = logger;
    }

    public IReadOnlyList<WorkflowRun> Active => _active.Values.Select(v => v.Run).ToList();

    public event Action<WorkflowRun>? RunUpdated;

    public async Task<WorkflowRun> StartAsync(
        string workflowId,
        IReadOnlyDictionary<string, string> inputs,
        string triggeredBy,
        CancellationToken ct = default)
    {
        var (workflow, run) = await CreateRunAsync(workflowId, inputs, triggeredBy, ct);
        var cts = new CancellationTokenSource();
        _active[run.Id] = (run, cts);

        _ = Task.Run(() => ExecuteAsync(workflow, run, cts.Token), CancellationToken.None);
        return run;
    }

    public async Task<WorkflowRun> RunAsync(
        string workflowId,
        IReadOnlyDictionary<string, string> inputs,
        string triggeredBy,
        CancellationToken ct = default)
    {
        var (workflow, run) = await CreateRunAsync(workflowId, inputs, triggeredBy, ct);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active[run.Id] = (run, cts);

        await ExecuteAsync(workflow, run, cts.Token);
        return run;
    }

    public async Task CancelAsync(string runId)
    {
        if (_active.TryGetValue(runId, out var entry))
        {
            await entry.Cts.CancelAsync();
            foreach (var step in entry.Run.Steps.Where(s => s.SessionId is not null && s.Status == RunStatus.Running))
                await _sessions.CancelAsync(step.SessionId!);
        }
    }

    public async Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(string? workflowId = null, CancellationToken ct = default)
    {
        var all = await _runs.GetAllAsync(ct);
        return all
            .Select(r => _active.TryGetValue(r.Id, out var live) ? live.Run : r)
            .Where(r => workflowId is null || r.WorkflowId == workflowId)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
    }

    public async Task<WorkflowRun?> GetRunAsync(string runId, CancellationToken ct = default) =>
        _active.TryGetValue(runId, out var live) ? live.Run : await _runs.GetAsync(runId, ct);

    private async Task<(Workflow Workflow, WorkflowRun Run)> CreateRunAsync(
        string workflowId,
        IReadOnlyDictionary<string, string> inputs,
        string triggeredBy,
        CancellationToken ct)
    {
        var workflow = await _workflows.GetAsync(workflowId, ct)
                       ?? throw new InvalidOperationException($"Workflow '{workflowId}' no longer exists.");

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declared in workflow.Inputs)
        {
            var supplied = inputs.TryGetValue(declared.Name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : declared.DefaultValue ?? string.Empty;

            if (declared.Required && string.IsNullOrWhiteSpace(supplied))
                throw new InvalidOperationException($"Input '{declared.Name}' is required.");

            resolved[declared.Name] = supplied;
        }

        // Anything passed in but not declared is still usable in templates.
        foreach (var pair in inputs)
            resolved.TryAdd(pair.Key, pair.Value);

        var run = new WorkflowRun
        {
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            ProjectId = inputs.TryGetValue("projectId", out var projectOverride) && !string.IsNullOrWhiteSpace(projectOverride)
                ? projectOverride
                : workflow.ProjectId,
            Inputs = resolved,
            TriggeredBy = triggeredBy,
            Status = RunStatus.Pending,
            Steps = workflow.Steps
                .OrderBy(s => s.Order)
                .Select(s => new WorkflowStepRun
                {
                    StepId = s.Id,
                    StepName = s.Name,
                    Order = s.Order,
                })
                .ToList(),
        };

        await _runs.UpsertAsync(run, ct);
        return (workflow, run);
    }

    private async Task ExecuteAsync(Workflow workflow, WorkflowRun run, CancellationToken ct)
    {
        run.Status = RunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        await PersistAsync(run);

        // The global context every step can read from.
        var context = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in run.Inputs)
        {
            context[input.Key] = input.Value;
            context[$"input.{input.Key}"] = input.Value;
        }
        context["workflow.name"] = workflow.Name;
        context["run.id"] = run.Id;
        context["previous"] = string.Empty;

        string? sharedWorkspace = null;

        try
        {
            foreach (var group in workflow.Steps.GroupBy(s => s.Order).OrderBy(g => g.Key))
            {
                ct.ThrowIfCancellationRequested();

                var stepTasks = group
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(step => ExecuteStepAsync(step, run, context, sharedWorkspace, ct))
                    .ToList();

                var outcomes = await Task.WhenAll(stepTasks);

                foreach (var outcome in outcomes)
                {
                    if (outcome.Workspace is not null)
                        sharedWorkspace ??= outcome.Workspace;

                    // Published globally: later steps reference it by name, whatever the distance.
                    context[$"steps.{TemplateRenderer.Slugify(outcome.StepName)}"] = outcome.Output;
                    context["previous"] = outcome.Output;
                }

                var blocking = outcomes.FirstOrDefault(o => o.Failed && !o.ContinueOnError);
                if (blocking is not null)
                {
                    run.Status = RunStatus.Failed;
                    run.Error = $"Step '{blocking.StepName}' failed: {blocking.Error}";
                    break;
                }
            }

            if (run.Status == RunStatus.Running)
                run.Status = RunStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Cancelled;
            run.Error = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow run {RunId} failed", run.Id);
            run.Status = RunStatus.Failed;
            run.Error = ex.Message;
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            await PersistAsync(run);
            _active.TryRemove(run.Id, out _);
        }
    }

    private sealed record StepOutcome(string StepName, string Output, bool Failed, bool ContinueOnError, string? Error, string? Workspace);

    private async Task<StepOutcome> ExecuteStepAsync(
        WorkflowStep step,
        WorkflowRun run,
        ConcurrentDictionary<string, string> context,
        string? sharedWorkspace,
        CancellationToken ct)
    {
        var stepRun = run.Steps.First(s => s.StepId == step.Id);
        stepRun.Status = RunStatus.Running;
        stepRun.StartedAt = DateTimeOffset.UtcNow;
        await PersistAsync(run);

        try
        {
            var prompt = TemplateRenderer.Render(step.PromptTemplate, context);

            var session = await _sessions.RunToCompletionAsync(
                new StartSessionRequest
                {
                    AgentId = step.AgentId,
                    Prompt = prompt,
                    Title = $"{run.WorkflowName} · {step.Name}",
                    Trigger = SessionTrigger.Workflow,
                    ProjectId = run.ProjectId,
                    WorkflowRunId = run.Id,
                    WorkingDirectoryOverride = step.ReuseWorkspace ? sharedWorkspace : null,
                },
                TimeSpan.FromMinutes(Math.Max(1, step.TimeoutMinutes)),
                ct);

            var output = session.Messages
                .Where(m => m.Role == MessageRole.Agent && !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => m.Content)
                .LastOrDefault() ?? string.Empty;

            stepRun.SessionId = session.Id;
            stepRun.Output = output;
            stepRun.FinishedAt = DateTimeOffset.UtcNow;

            var failed = session.Status is SessionStatus.Failed or SessionStatus.Cancelled;
            stepRun.Status = failed ? RunStatus.Failed : RunStatus.Succeeded;
            stepRun.Error = failed ? session.LastError ?? "The agent did not finish." : null;
            await PersistAsync(run);

            return new StepOutcome(step.Name, output, failed, step.ContinueOnError, stepRun.Error, session.WorkingDirectory);
        }
        catch (Exception ex)
        {
            stepRun.Status = RunStatus.Failed;
            stepRun.Error = ex.Message;
            stepRun.FinishedAt = DateTimeOffset.UtcNow;
            await PersistAsync(run);
            return new StepOutcome(step.Name, string.Empty, true, step.ContinueOnError, ex.Message, null);
        }
    }

    private async Task PersistAsync(WorkflowRun run)
    {
        try
        {
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await _runs.UpsertAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist workflow run {RunId}", run.Id);
        }

        RunUpdated?.Invoke(run);
    }
}
