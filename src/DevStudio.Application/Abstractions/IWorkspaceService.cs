using DevStudio.Domain.Agents;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Sessions;

namespace DevStudio.Application.Abstractions;

/// <summary>Where a session will run, which worktree it owns, and which project it belongs to.</summary>
public sealed record SessionWorkspace(string Path, string? RepositoryId, Worktree? Worktree, string? ProjectId = null);

/// <summary>A project file carried to wherever the workspace is being built, contents and all.</summary>
public sealed record SuppliedFile(string FileName, byte[] Content);

/// <summary>
/// A workspace request with nothing left to look up: the repository and base branch are already
/// chosen, and any project files come with their bytes attached.
///
/// This exists because a session can be prepared on another machine. Over there the agent's project
/// does not exist — projects are orchestration, and orchestration stays with the session — so
/// everything the project contributes has to be resolved on this side first and carried across.
/// The local path resolves its own plan and then follows exactly the same code, which is what keeps
/// the two from drifting.
/// </summary>
public sealed record WorkspacePlan
{
    public required Agent Agent { get; init; }
    public required string SessionId { get; init; }

    /// <summary>Repository to work in, already resolved from the agent and its project.</summary>
    public string? RepositoryId { get; init; }

    /// <summary>Branch a worktree is cut from, already resolved. Null falls back to the repo default.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>Only used to name the fallback workspace directory when there is no repository.</summary>
    public string? ProjectId { get; init; }

    /// <summary>MCP servers the session brings beyond the agent's own.</summary>
    public IReadOnlyList<string>? ExtraServerIds { get; init; }

    /// <summary>
    /// The project's uploaded files. Supplied explicitly so a remote build gets them without
    /// needing the project; a local build reads its own and passes them the same way.
    /// </summary>
    public IReadOnlyList<SuppliedFile> ProjectFiles { get; init; } = [];
}

/// <summary>
/// Prepares the directory an agent session runs in: worktree or project folder, the agent's skills,
/// its MCP configuration, and the project's uploaded files.
/// </summary>
public interface IWorkspaceService
{
    Task<SessionWorkspace> PrepareAsync(Agent agent, string sessionId, string? projectId, CancellationToken ct = default);

    /// <summary>Prepares a workspace whose session brings MCP servers of its own.</summary>
    Task<SessionWorkspace> PrepareAsync(
        Agent agent,
        string sessionId,
        string? projectId,
        IReadOnlyList<string>? extraServerIds,
        CancellationToken ct = default);

    /// <summary>
    /// Prepares a workspace from a plan that has already been resolved. Every other overload ends
    /// up here; a remote host is handed the plan directly because it cannot resolve one itself.
    /// </summary>
    Task<SessionWorkspace> PrepareAsync(WorkspacePlan plan, CancellationToken ct = default);

    /// <summary>
    /// Turns an agent and project into a plan by reading the local stores. Called before dispatching
    /// work to another machine, which is why it is separate from preparing: the resolution happens
    /// here, the building happens over there.
    /// </summary>
    Task<WorkspacePlan> PlanAsync(
        Agent agent,
        string sessionId,
        string? projectId,
        IReadOnlyList<string>? extraServerIds,
        CancellationToken ct = default);

    /// <summary>Releases a worktree, deleting it when it was created for this session and pruning is on.</summary>
    Task ReleaseAsync(SessionWorkspace workspace, CancellationToken ct = default);

    /// <summary>Writes the agent's enabled skills into the workspace for the CLI to discover.</summary>
    Task MaterialiseSkillsAsync(Agent agent, string workspacePath, CancellationToken ct = default);

    /// <summary>
    /// Writes .mcp.json for the agent's selected MCP servers, plus any attached to the session
    /// itself.
    /// </summary>
    /// <returns>The names of the servers written, for allow-listing their tools.</returns>
    Task<IReadOnlyList<string>> MaterialiseMcpAsync(
        Agent agent,
        string workspacePath,
        IReadOnlyList<string>? extraServerIds = null,
        CancellationToken ct = default);

    /// <summary>Copies a project's uploaded files into the workspace so agents can read them.</summary>
    Task MaterialiseProjectFilesAsync(string projectId, string workspacePath, CancellationToken ct = default);

    /// <summary>Copies the global library into the workspace as ./global-files.</summary>
    Task MaterialiseGlobalFilesAsync(string workspacePath, CancellationToken ct = default);

    /// <summary>
    /// Agent instructions with the project's instructions layered on top. When a session id is
    /// supplied the agent is also told how to pull guidance for that session over MCP.
    /// </summary>
    /// <param name="tactics">
    /// Token-saving tactics in force for the turn, resolved from the agent and whatever the
    /// conversation has overridden. Passed per turn rather than read off the agent so a tactic
    /// switched on mid-chat lands on the next message.
    /// </param>
    /// <param name="handoverModel">
    /// The cheaper model the agent may move itself onto, when there is one and it has not already
    /// asked. Null leaves the marker unmentioned, because a marker with nowhere to go is an
    /// instruction that would quietly do nothing.
    /// </param>
    Task<string> ComposeSystemPromptAsync(
        Agent agent,
        string? projectId,
        string? sessionId = null,
        TokenTactics tactics = TokenTactics.None,
        string? handoverModel = null,
        CancellationToken ct = default);

    /// <summary>
    /// Writes outstanding guidance to GUIDANCE.md in the workspace, so an agent that reads its own
    /// working directory can pick up a steer without any MCP server configured.
    /// </summary>
    Task WriteGuidanceAsync(string workspacePath, IEnumerable<GuidanceMessage> guidance, CancellationToken ct = default);
}
