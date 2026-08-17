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

    /// <summary>
    /// Host directories mounted into the container that may be registered as repositories and
    /// browsed from the UI. This is what lets an agent work in the same checkout an IDE on the host
    /// has open: bind mount the folder, list it here, and attach the repo from the Repositories
    /// page. Empty means the feature is off — nothing outside the data volume is reachable.
    /// </summary>
    public List<string> LocalRepositoryRoots { get; set; } = [];

    /// <summary>
    /// Offers every drive on the machine — <c>C:\</c>, <c>D:\</c>, and so on — in the folder picker,
    /// on top of <see cref="LocalRepositoryRoots"/>. Right for the desktop build, which is running as
    /// the user and has no container boundary to protect: the repository they want to attach is as
    /// likely to be on another drive as under their home directory. Left false for the container,
    /// where the only reachable paths should be the bind mounts the operator chose to declare.
    /// </summary>
    public bool AllowAllLocalDrives { get; set; }

    /// <summary>
    /// Folder, created beside a host-mounted repository, that worktrees for it are cut into. They
    /// have to stay on the mount or the IDE cannot see them; beside the repo rather than inside it
    /// so nothing shows up as untracked in the checkout being edited.
    /// </summary>
    public string LocalWorktreesFolderName { get; set; } = ".devstudio-worktrees";

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

    /// <summary>
    /// Says that this container is the isolation, so the CLIs should not each build a sandbox
    /// inside it. Both of them use bubblewrap, which needs unprivileged user namespaces — a host
    /// that disables those (Docker's default seccomp profile among them) fails to create the
    /// namespace, and every command dies before it starts. That reads as the tool being broken
    /// rather than the sandbox being unavailable, which is what makes it so hard to place.
    ///
    /// True is right for the intended deployment, where the container, the per-session worktree and
    /// the permission mode are the isolation. Set it false when running the orchestrator directly
    /// on a machine you care about, or when the container is allowed to create namespaces and the
    /// CLIs' own sandboxes can do their job.
    /// </summary>
    public bool ContainerIsTheSandbox { get; set; } = true;

    /// <summary>
    /// Tools every agent may use without being asked, in the CLI's own permission syntax. These are
    /// the local commands the container exists to provide: an agent that has to stop and ask before
    /// running <c>glab</c> gets no answer at all when nobody is watching, and reports the tool as
    /// broken. Anything approved from the UI is added to this per session.
    ///
    /// <c>WebFetch</c>/<c>WebSearch</c> are not listed here — they are the same kind of rule, but
    /// gated behind <see cref="GlobalSettings.EnableWebTools"/> so an operator can turn real network
    /// access for agents on or off, and <see cref="SessionManager"/> adds them when it is on.
    /// </summary>
    public List<string> DefaultAllowedTools { get; set; } =
    [
        "Bash(glab:*)",
        "Bash(gh:*)",
        "Bash(git:*)",
    ];

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

    /// <summary>
    /// How often the queue dispatcher looks for items to start. Shorter than the scheduler's tick
    /// because a queue is drained as fast as its slots allow, not on a clock — this is the delay
    /// between an item arriving and work starting on it.
    /// </summary>
    public int QueueTickSeconds { get; set; } = 10;

    /// <summary>
    /// Whether to ask GitHub, every six hours, whether a newer release exists — and say so in the
    /// UI. A container cannot update itself, so this only ever tells somebody; pulling the new image
    /// stays the operator's decision. The desktop builds turn it off, because they update themselves.
    /// Set false for an install that should not be reaching the internet on its own.
    /// </summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>Repository the update check reads releases from, as <c>owner/name</c>.</summary>
    public string UpdateRepository { get; set; } = "matthewratcliffe/devstudio";

    /// <summary>
    /// How old a finished session may get before it is archived on its own, in hours. The sessions
    /// list is a working view of what is happening now, and a machine left running fills it with
    /// conversations nobody has looked at since yesterday. Zero turns the sweep off entirely.
    ///
    /// Only sessions that have stopped are touched — a run still going is by definition current,
    /// however long it has been at it — and a session somebody has restored from the archive is
    /// never taken again.
    /// </summary>
    public int SessionAutoArchiveHours { get; set; } = 24;

    /// <summary>
    /// How long a conversation may sit with nothing said in it before it is finished on its own, in
    /// hours. A chat left open can be picked up again much later against a workspace that has moved
    /// on underneath it, and one abandoned mid-thought reads as current work until somebody closes
    /// it. Zero turns this off and leaves conversations open until they are ended by hand.
    ///
    /// Only idle conversations are touched: an agent still working is not idle however long its
    /// turn has been running.
    /// </summary>
    public int SessionIdleFinishHours { get; set; } = 4;

    /// <summary>
    /// How often the housekeeping sweeps run. Sessions age in hours, so there is nothing to gain
    /// from looking more often than this.
    /// </summary>
    public int SessionArchiveTickMinutes { get; set; } = 15;

    /// <summary>Delete ephemeral worktrees when their session finishes.</summary>
    public bool PruneEphemeralWorktrees { get; set; } = false;
}
