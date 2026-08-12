using AiShop.Domain.Common;
using AiShop.Domain.Providers;

namespace AiShop.Domain.Agents;

/// <summary>How much the CLI is allowed to do without a human confirming each step.</summary>
public enum PermissionMode
{
    /// <summary>Every tool use needs approval. Safe, but blocks unattended runs.</summary>
    Default = 0,
    /// <summary>Read-only analysis; the agent plans but does not edit.</summary>
    Plan = 1,
    /// <summary>File edits and safe commands are auto-approved.</summary>
    AcceptEdits = 2,
    /// <summary>No prompts at all. Only sensible inside a disposable worktree.</summary>
    Unrestricted = 3,
}

/// <summary>A reusable agent definition: which CLI to drive, with what instructions, where.</summary>
public sealed class Agent : Entity
{
    public string Name { get; set; } = "New agent";
    public string Description { get; set; } = string.Empty;
    public AiProvider Provider { get; set; } = AiProvider.Claude;

    /// <summary>Which user-defined CLI to drive when <see cref="Provider"/> is Custom.</summary>
    public string? CliProviderId { get; set; }

    /// <summary>Model slug passed straight to the CLI (--model). Empty means the CLI default.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// How hard the model should think. Claude takes it as --effort; codex as its
    /// model_reasoning_effort config. Empty leaves the CLI's own default alone.
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>Prepended to the first prompt of every session this agent starts.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    public PermissionMode PermissionMode { get; set; } = PermissionMode.AcceptEdits;

    /// <summary>Project the agent belongs to. Its instructions and files travel with every session.</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Pins this agent to one logged-in account. The project's choice wins when the session runs in
    /// a project; this is what covers agents working outside one.
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>Repository the agent works in. Null means the project or scratch workspace.</summary>
    public string? RepositoryId { get; set; }

    /// <summary>When set with a repository, each session gets its own worktree off this branch.</summary>
    public string? BaseBranch { get; set; }

    /// <summary>Give every session an isolated worktree so concurrent agents cannot collide.</summary>
    public bool UseWorktree { get; set; } = true;

    /// <summary>Skills copied into the session workspace before the CLI starts.</summary>
    public List<string> SkillIds { get; set; } = [];

    /// <summary>MCP servers wired up for this agent, on top of any marked as default.</summary>
    public List<string> McpServerIds { get; set; } = [];

    /// <summary>Extra environment variables handed to the CLI process.</summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>Extra raw CLI arguments, appended after the generated ones.</summary>
    public string? ExtraArguments { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Accent used for this agent's cards and chat bubbles.</summary>
    public string Accent { get; set; } = "cyan";

    /// <summary>
    /// Created on demand to back the quick chat. Hidden from the Agents page: it is an implementation
    /// detail of "just talk to a model", not something to configure.
    /// </summary>
    public bool IsQuickChat { get; set; }
}
