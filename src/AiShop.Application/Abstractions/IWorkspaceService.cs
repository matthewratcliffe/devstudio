using AiShop.Domain.Agents;
using AiShop.Domain.Repositories;
using AiShop.Domain.Sessions;

namespace AiShop.Application.Abstractions;

/// <summary>Where a session will run, which worktree it owns, and which project it belongs to.</summary>
public sealed record SessionWorkspace(string Path, string? RepositoryId, Worktree? Worktree, string? ProjectId = null);

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
    Task<string> ComposeSystemPromptAsync(
        Agent agent,
        string? projectId,
        string? sessionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Writes outstanding guidance to GUIDANCE.md in the workspace, so an agent that reads its own
    /// working directory can pick up a steer without any MCP server configured.
    /// </summary>
    Task WriteGuidanceAsync(string workspacePath, IEnumerable<GuidanceMessage> guidance, CancellationToken ct = default);
}
