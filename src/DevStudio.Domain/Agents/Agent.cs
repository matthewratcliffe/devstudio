using DevStudio.Domain.Common;
using DevStudio.Domain.Providers;

namespace DevStudio.Domain.Agents;

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

    /// <summary>
    /// Model for the first <see cref="OpeningTurns"/> turns of a session, before it hands over to
    /// <see cref="Model"/>. The opening of a conversation is where the plan is made and where a
    /// stronger model earns its cost; the turns after it are usually carrying that plan out. Empty
    /// means there is no handover and <see cref="Model"/> runs the whole session.
    /// </summary>
    public string? OpeningModel { get; set; }

    /// <summary>Thinking level for the opening turns. Empty falls back to <see cref="Effort"/>.</summary>
    public string? OpeningEffort { get; set; }

    /// <summary>
    /// How many turns the opening model covers. Zero — the default — means no handover at all.
    /// </summary>
    public int OpeningTurns { get; set; }

    /// <summary>Prepended to the first prompt of every session this agent starts.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// The first prompt to send when a session starts without one of its own. Unlike the system
    /// prompt this is an ordinary turn the agent answers, which is what lets an agent with a fixed
    /// job — a nightly triage, a standing review — run with nothing typed in.
    /// </summary>
    public string DefaultPrompt { get; set; } = string.Empty;

    public PermissionMode PermissionMode { get; set; } = PermissionMode.AcceptEdits;

    /// <summary>
    /// Cost-saving ways of working this agent is told to adopt, composed into the system prompt as
    /// its own section. None — the default — leaves the CLI to work however it normally would. A
    /// conversation can override the selection for itself at any point.
    /// </summary>
    public TokenTactics TokenMinimisation { get; set; } = TokenTactics.None;

    /// <summary>Project the agent belongs to. Its instructions and files travel with every session.</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Pins this agent to one logged-in account. The project's choice wins when the session runs in
    /// a project; this is what covers agents working outside one.
    /// </summary>
    public string? AccountId { get; set; }

    /// <summary>Optional account to use when the primary account is unavailable or out of capacity.</summary>
    public string? FallbackAccountId { get; set; }

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
    /// Path, relative to the team settings repository, of the file this was imported from. Set means
    /// the repository owns it: the next sync rewrites it, and deleting the file deletes it. Null is a
    /// local definition, which no sync ever touches.
    /// </summary>
    public string? TeamSourcePath { get; set; }

    /// <summary>
    /// Created on demand to back the quick chat. Hidden from the Agents page: it is an implementation
    /// detail of "just talk to a model", not something to configure.
    /// </summary>
    public bool IsQuickChat { get; set; }
}
