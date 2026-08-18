using DevStudio.Domain.Common;

namespace DevStudio.Domain.Workflows;

/// <summary>A repeatable sequence of agent prompts. Steps sharing an order run concurrently.</summary>
public sealed class Workflow : Entity
{
    public string Name { get; set; } = "New workflow";
    public string Description { get; set; } = string.Empty;

    /// <summary>Groups this workflow in pick lists. Empty falls into an "Uncategorized" bucket.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Declared inputs, referenced in prompts as {{name}} and supplied at run time.</summary>
    public List<WorkflowInput> Inputs { get; set; } = [];
    public List<WorkflowStep> Steps { get; set; } = [];
    /// <summary>Default project for every step that does not name its own.</summary>
    public string? ProjectId { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path, relative to the team settings repository, of the file this was imported from. Set means
    /// the repository owns it: the next sync rewrites it, and deleting the file deletes it. Null is a
    /// local definition, which no sync ever touches.
    /// </summary>
    public string? TeamSourcePath { get; set; }
}

public sealed class WorkflowInput
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? DefaultValue { get; set; }
    public bool Required { get; set; } = true;
}

public sealed class WorkflowStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "Step";
    /// <summary>Steps sharing an order run at the same time; the next order waits for all of them.</summary>
    public int Order { get; set; }
    public string AgentId { get; set; } = string.Empty;
    /// <summary>Supports {{input}} placeholders plus {{previous}} and {{steps.step-name}}.</summary>
    public string PromptTemplate { get; set; } = string.Empty;
    public bool ContinueOnError { get; set; }
    /// <summary>Reuse the previous step's worktree instead of cutting a fresh one.</summary>
    public bool ReuseWorkspace { get; set; } = true;
    public int TimeoutMinutes { get; set; } = 30;
}
