using DevStudio.Domain.Common;
using DevStudio.Domain.Providers;

namespace DevStudio.Domain.Projects;

/// <summary>
/// A body of work. Everything scoped to a project — agents, sessions, workflows, schedules —
/// inherits its instructions and gets its uploaded files on disk in the workspace.
/// </summary>
public sealed class Project : Entity
{
    public string Name { get; set; } = "New project";
    public string Description { get; set; } = string.Empty;

    /// <summary>Prepended to the system prompt of every agent run inside this project.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// The one AI provider this project's work runs on. Every session in the project uses it,
    /// whatever an individual agent is configured with, so a project cannot end up half on one CLI
    /// and half on another. Null leaves each agent to its own choice.
    /// </summary>
    public AiProvider? Provider { get; set; }

    /// <summary>Which user-defined CLI, when <see cref="Provider"/> is Custom.</summary>
    public string? CliProviderId { get; set; }

    /// <summary>
    /// The one forge this project's repositories live on. Several repositories are fine; they all
    /// come from here.
    /// </summary>
    public SourceControlProvider SourceControl { get; set; } = SourceControlProvider.GitHub;

    /// <summary>
    /// Which logged-in account each CLI should use for work in this project — a personal login for
    /// one project and a work login for another. Null falls back to the default account.
    /// </summary>
    public string? ClaudeAccountId { get; set; }
    public string? CodexAccountId { get; set; }
    public string? ClaudeFallbackAccountId { get; set; }
    public string? CodexFallbackAccountId { get; set; }

    /// <summary>Account per user-defined CLI, keyed by its provider id.</summary>
    public Dictionary<string, string> CliAccountIds { get; set; } = [];

    /// <summary>
    /// Summarise a session after this many turns, then start the CLI conversation again from the
    /// summary. Long chats otherwise carry their whole history into every turn, which gets slow and
    /// expensive and eventually pushes the early context out anyway. Zero turns it off.
    /// </summary>
    public int SummariseAfterTurns { get; set; }

    /// <summary>
    /// After summarising, drop the provider's conversation id so the next turn starts fresh with the
    /// summary as its context. Off means the summary is only recorded, and the chat keeps its history.
    /// </summary>
    public bool CompactAfterSummary { get; set; } = true;

    /// <summary>Overrides the built-in summary instruction when set.</summary>
    public string? SummaryPrompt { get; set; }

    /// <summary>Optional repository the project's work happens in.</summary>
    public string? RepositoryId { get; set; }

    /// <summary>Branch new worktrees are cut from for this project.</summary>
    public string? BaseBranch { get; set; }

    /// <summary>Uploaded reference material, stored under the project's folder on the volume.</summary>
    public List<StoredFile> Files { get; set; } = [];

    /// <summary>
    /// Whether the global standards and files apply here. On by default — turn it off for a project
    /// that deliberately plays by different rules.
    /// </summary>
    public bool InheritGlobalInstructions { get; set; } = true;

    public string Accent { get; set; } = "cyan";
    public bool Archived { get; set; }
}
