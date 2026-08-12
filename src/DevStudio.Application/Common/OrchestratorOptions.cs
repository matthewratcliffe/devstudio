namespace DevStudio.Application.Common;

/// <summary>
/// Everything environment-specific in one place. Paths default to the container layout and are
/// overridden from configuration when running on a developer machine.
/// </summary>
public sealed class OrchestratorOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>Root of the persistent volume holding JSON state.</summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>Where cloned repositories live.</summary>
    public string RepositoriesPath { get; set; } = "/data/repos";

    /// <summary>Where per-session worktrees are cut.</summary>
    public string WorktreesPath { get; set; } = "/data/worktrees";

    /// <summary>Workspace used by agents that are not bound to a repository.</summary>
    public string ScratchPath { get; set; } = "/data/scratch";

    /// <summary>Home directory of the container user; holds ~/.claude and ~/.codex credentials.</summary>
    public string HomePath { get; set; } = "/home/orchestrator";

    public string ClaudeExecutable { get; set; } = "claude";
    public string CodexExecutable { get; set; } = "codex";
    public string GitExecutable { get; set; } = "git";
    public string GitHubCliExecutable { get; set; } = "gh";
    public string GitLabCliExecutable { get; set; } = "glab";

    /// <summary>
    /// GitLab host commands are issued against. Override for a self-managed instance —
    /// <c>Orchestrator__GitLabHost=gitlab.mycompany.com</c>.
    /// </summary>
    public string GitLabHost { get; set; } = "gitlab.com";

    /// <summary>GitHub host, for GitHub Enterprise Server.</summary>
    public string GitHubHost { get; set; } = "github.com";

    /// <summary>Port this app listens on, used for the built-in MCP server's own URL.</summary>
    public int HttpPort { get; set; } = 7080;

    /// <summary>
    /// Port the CLI OAuth listeners bind to inside the container. Codex uses 1455 and hard-codes it
    /// into its redirect URI, so the orchestrator forwards callbacks it receives to this port.
    /// </summary>
    public int CliCallbackPort { get; set; } = 1455;

    /// <summary>
    /// Model choices offered for each provider. Editable here because model names change faster
    /// than this app does; the UI always keeps a free-text option as well.
    /// </summary>
    public List<string> ClaudeModels { get; set; } = ["opus", "sonnet", "haiku", "fable"];

    public List<string> CodexModels { get; set; } = ["gpt-5-codex", "gpt-5", "o3", "o4-mini"];

    /// <summary>Effort levels the claude CLI accepts for --effort.</summary>
    public List<string> ClaudeEfforts { get; set; } = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Reasoning levels passed to codex as model_reasoning_effort.</summary>
    public List<string> CodexEfforts { get; set; } = ["minimal", "low", "medium", "high"];

    /// <summary>Hard cap on agent turns running at once.</summary>
    public int MaxConcurrentSessions { get; set; } = 6;

    /// <summary>A turn is abandoned after this long with no exit.</summary>
    public int TurnTimeoutMinutes { get; set; } = 60;

    /// <summary>How often the scheduler wakes up to look for due schedules.</summary>
    public int SchedulerTickSeconds { get; set; } = 20;

    /// <summary>Delete ephemeral worktrees when their session finishes.</summary>
    public bool PruneEphemeralWorktrees { get; set; } = false;
}
