using System.Text.Json;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Remoting;
using DevStudio.Domain.Repositories;
using DevStudio.Domain.Sessions;
using Microsoft.AspNetCore.SignalR.Client;

namespace DevStudio.Infrastructure.Remoting;

/// <summary>
/// A workspace on another machine. The paths that come back are that machine's and are never touched
/// here — they go straight into the turn request and back over the wire.
///
/// Only the two methods a session actually drives are remoted. The rest throw rather than quietly
/// doing nothing locally, because a workspace half-built here and half over there is the kind of
/// fault that shows up as an agent reading the wrong repository.
/// </summary>
public sealed class RemoteWorkspaceService : IWorkspaceService
{
    private readonly RemoteInstance _instance;
    private readonly IRemoteConnectionPool _pool;
    private readonly IWorkspaceService _local;

    public RemoteWorkspaceService(RemoteInstance instance, IRemoteConnectionPool pool, IWorkspaceService local)
    {
        _instance = instance;
        _pool = pool;
        _local = local;
    }

    /// <summary>
    /// Planning is a local act — it reads the local agent and project — so it stays local even when
    /// the building will not be. This is the join between the two sides.
    /// </summary>
    public Task<WorkspacePlan> PlanAsync(
        Agent agent,
        string sessionId,
        string? projectId,
        IReadOnlyList<string>? extraServerIds,
        CancellationToken ct = default) =>
        _local.PlanAsync(agent, sessionId, projectId, extraServerIds, ct);

    public async Task<SessionWorkspace> PrepareAsync(WorkspacePlan plan, CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);
        var result = await connection.InvokeAsync<RemoteWorkspace>(RemoteHubMethods.PrepareWorkspace, plan, ct);

        return new SessionWorkspace(
            result.Path,
            result.RepositoryId,
            result.WorktreeJson is null ? null : JsonSerializer.Deserialize<Worktree>(result.WorktreeJson),
            result.ProjectId);
    }

    public async Task<SessionWorkspace> PrepareAsync(
        Agent agent,
        string sessionId,
        string? projectId,
        CancellationToken ct = default) =>
        await PrepareAsync(agent, sessionId, projectId, null, ct);

    public async Task<SessionWorkspace> PrepareAsync(
        Agent agent,
        string sessionId,
        string? projectId,
        IReadOnlyList<string>? extraServerIds,
        CancellationToken ct = default) =>
        await PrepareAsync(await PlanAsync(agent, sessionId, projectId, extraServerIds, ct), ct);

    public async Task ReleaseAsync(SessionWorkspace workspace, CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);

        await connection.InvokeAsync(
            RemoteHubMethods.ReleaseWorkspace,
            new RemoteWorkspace(
                workspace.Path,
                workspace.RepositoryId,
                workspace.Worktree?.Id,
                workspace.Worktree is null ? null : JsonSerializer.Serialize(workspace.Worktree),
                workspace.ProjectId),
            ct);
    }

    public async Task MaterialiseSkillsAsync(Agent agent, string workspacePath, CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);
        await connection.InvokeAsync(RemoteHubMethods.MaterialiseSkills, agent, workspacePath, ct);
    }

    public async Task<IReadOnlyList<string>> MaterialiseMcpAsync(
        Agent agent,
        string workspacePath,
        IReadOnlyList<string>? extraServerIds = null,
        CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);

        return await connection.InvokeAsync<IReadOnlyList<string>>(
            RemoteHubMethods.MaterialiseMcp,
            agent,
            workspacePath,
            extraServerIds ?? [],
            ct);
    }

    public async Task WriteGuidanceAsync(
        string workspacePath,
        IEnumerable<GuidanceMessage> guidance,
        CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);
        await connection.InvokeAsync(RemoteHubMethods.WriteGuidance, workspacePath, guidance.ToList(), ct);
    }

    /// <summary>
    /// Composed locally and carried in the turn request. It is built from the agent, the project and
    /// the global settings — all local — so there is nothing over there to compose it from.
    /// </summary>
    public Task<string> ComposeSystemPromptAsync(
        Agent agent,
        string? projectId,
        string? sessionId = null,
        TokenTactics tactics = TokenTactics.None,
        string? handoverModel = null,
        CancellationToken ct = default) =>
        _local.ComposeSystemPromptAsync(agent, projectId, sessionId, tactics, handoverModel, ct);

    /// <summary>Carried inside the plan, with the bytes, so there is nothing to fetch remotely.</summary>
    public Task MaterialiseProjectFilesAsync(string projectId, string workspacePath, CancellationToken ct = default) =>
        Task.CompletedTask;

    public async Task MaterialiseGlobalFilesAsync(string workspacePath, CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);
        await connection.InvokeAsync(RemoteHubMethods.MaterialiseGlobalFiles, workspacePath, ct);
    }
}

/// <summary>
/// Which login a CLI runs as over there. Resolved remotely because the home directories it chooses
/// between are that machine's, and a path from this one would name nothing.
/// </summary>
public sealed class RemoteAccountService : IAccountService
{
    private readonly RemoteInstance _instance;
    private readonly IRemoteConnectionPool _pool;

    public RemoteAccountService(RemoteInstance instance, IRemoteConnectionPool pool)
    {
        _instance = instance;
        _pool = pool;
    }

    public async Task<ResolvedAccount> ResolveAsync(Agent agent, string? projectId, CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);
        var result = await connection.InvokeAsync<RemoteAccountResult>(RemoteHubMethods.ResolveAccount, agent, ct);

        return Convert(result);
    }

    private static ResolvedAccount Convert(RemoteAccountResult result) =>
        new(result.AccountId,
            result.Name,
            result.HomePath,
            result.Fallback is null ? null : Convert(result.Fallback));

    /// <summary>
    /// Accounts are created and deleted where their credentials live. Doing it from here would make
    /// a directory on the wrong machine and a record that points at nothing.
    /// </summary>
    public Task<string> GetHomePathAsync(string accountId, CancellationToken ct = default) =>
        throw Managed();

    public Task<Domain.Providers.ProviderAccount> CreateAsync(
        string name,
        Domain.Providers.AiProvider provider,
        CancellationToken ct = default) =>
        throw Managed();

    public Task<Domain.Providers.ProviderAccount> CreateAsync(
        string name,
        Domain.Providers.AiProvider provider,
        string? cliProviderId,
        CancellationToken ct = default) =>
        throw Managed();

    public Task<bool> DeleteAsync(string accountId, bool deleteCredentials, CancellationToken ct = default) =>
        throw Managed();

    public Task<IReadOnlyList<Domain.Providers.ProviderAccount>> RefreshAuthStateAsync(CancellationToken ct = default) =>
        throw Managed();

    private InvalidOperationException Managed() =>
        new($"Logins for {_instance.Name} are managed on that machine, from its own Logins page.");
}

/// <summary>Reads what an agent left in a workspace on the far side, for the files panel.</summary>
public sealed class RemoteWorkspaceFileService : IWorkspaceFileService
{
    private readonly RemoteInstance _instance;
    private readonly IRemoteConnectionPool _pool;

    public RemoteWorkspaceFileService(RemoteInstance instance, IRemoteConnectionPool pool)
    {
        _instance = instance;
        _pool = pool;
    }

    public async Task<IReadOnlyList<WorkspaceFile>> ListAsync(
        string sessionId,
        int limit = 200,
        CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);

        var files = await connection.InvokeAsync<IReadOnlyList<RemoteWorkspaceFile>>(
            RemoteHubMethods.ListWorkspaceFiles,
            sessionId,
            limit,
            ct);

        return files
            .Select(f => new WorkspaceFile(f.RelativePath, f.Name, f.SizeBytes, f.ModifiedAt, f.IsImage, f.IsText))
            .ToList();
    }

    /// <summary>
    /// Whole-file rather than streamed. A hub method cannot hand back a live stream, and the
    /// alternative — chunking by hand — buys nothing for files a person is about to look at, which
    /// is what this panel is for.
    /// </summary>
    public async Task<(Stream Content, string FileName, string ContentType)?> OpenAsync(
        string sessionId,
        string relativePath,
        CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);

        var file = await connection.InvokeAsync<RemoteFileContent?>(
            RemoteHubMethods.ReadWorkspaceFile,
            sessionId,
            relativePath,
            ct);

        return file is null
            ? null
            : (new MemoryStream(file.Content), file.FileName, file.ContentType);
    }
}
