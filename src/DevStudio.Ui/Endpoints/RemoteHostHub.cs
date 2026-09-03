using System.Runtime.CompilerServices;
using System.Text.Json;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;
using DevStudio.Infrastructure.Remoting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DevStudio.Ui.Endpoints;

/// <summary>
/// The receiving end of a remote instance: another installation, holding an approved key, asking
/// this one to do work on its own machine.
///
/// Everything here runs against this instance's ordinary services — the same workspace service, the
/// same CLI registry, the same terminal service the local UI uses. There is no separate "remote
/// mode": a turn dispatched from another machine takes exactly the path a local one does, which is
/// what stops the two drifting into behaving differently.
/// </summary>
[Authorize(AuthenticationSchemes = RemoteAuth.Scheme, Policy = RemoteAuth.Policy)]
public sealed class RemoteHostHub : Hub
{
    private readonly IExecutionHost _local;
    private readonly IRemoteAccessService _access;
    private readonly ILogger<RemoteHostHub> _logger;

    public RemoteHostHub(IExecutionHost local, IRemoteAccessService access, ILogger<RemoteHostHub> logger)
    {
        _local = local;
        _access = access;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        await TouchAsync();
        _logger.LogInformation("Remote instance {Instance} connected", Context.User?.Identity?.Name ?? "unknown");
        await base.OnConnectedAsync();
    }

    /// <summary>What this machine offers, for the far side's dropdowns.</summary>
    public async Task<RemoteHostConfig> GetConfig()
    {
        await TouchAsync();

        return await _local.GetConfigAsync(Context.ConnectionAborted);
    }

    /// <summary>
    /// One turn, streamed. The events are yielded as the CLI produces them and SignalR carries each
    /// one as it is written, which is what makes a remote conversation read like a local one instead
    /// of arriving all at once when the process exits.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> RunTurn(
        AiProvider provider,
        string? cliProviderId,
        TurnRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await TouchAsync();

        var cli = await _local.Clis.ResolveAsync(provider, cliProviderId, ct);

        _logger.LogInformation(
            "Running a remote turn on {Cli} in {Directory}",
            cli.DisplayName,
            request.WorkingDirectory);

        await foreach (var evt in cli.RunTurnAsync(request, ct))
            yield return evt;
    }

    public async Task<ProviderAuthStatus> GetAuthStatus(AiProvider provider, string? cliProviderId, string? homePath)
    {
        var cli = await _local.Clis.ResolveAsync(provider, cliProviderId, Context.ConnectionAborted);

        return await cli.GetAuthStatusAsync(homePath, Context.ConnectionAborted);
    }

    public async Task<RemoteWorkspace> PrepareWorkspace(WorkspacePlan plan)
    {
        await TouchAsync();

        var workspace = await _local.Workspaces.PrepareAsync(plan, Context.ConnectionAborted);

        return new RemoteWorkspace(
            workspace.Path,
            workspace.RepositoryId,
            workspace.Worktree?.Id,
            workspace.Worktree is null ? null : JsonSerializer.Serialize(workspace.Worktree),
            workspace.ProjectId);
    }

    public Task ReleaseWorkspace(RemoteWorkspace workspace) =>
        _local.Workspaces.ReleaseAsync(
            new SessionWorkspace(
                workspace.Path,
                workspace.RepositoryId,
                workspace.WorktreeJson is null
                    ? null
                    : JsonSerializer.Deserialize<Domain.Repositories.Worktree>(workspace.WorktreeJson),
                workspace.ProjectId),
            Context.ConnectionAborted);

    public Task MaterialiseSkills(Agent agent, string workspacePath) =>
        _local.Workspaces.MaterialiseSkillsAsync(agent, workspacePath, Context.ConnectionAborted);

    public Task<IReadOnlyList<string>> MaterialiseMcp(
        Agent agent,
        string workspacePath,
        IReadOnlyList<string> extraServerIds) =>
        _local.Workspaces.MaterialiseMcpAsync(agent, workspacePath, extraServerIds, Context.ConnectionAborted);

    public Task MaterialiseGlobalFiles(string workspacePath) =>
        _local.Workspaces.MaterialiseGlobalFilesAsync(workspacePath, Context.ConnectionAborted);

    public Task WriteGuidance(string workspacePath, List<GuidanceMessage> guidance) =>
        _local.Workspaces.WriteGuidanceAsync(workspacePath, guidance, Context.ConnectionAborted);

    /// <summary>
    /// Which of this machine's logins the CLI runs as. The project is not passed: projects are the
    /// caller's, so the pin that matters here is the agent's own.
    /// </summary>
    public async Task<RemoteAccountResult> ResolveAccount(Agent agent)
    {
        var account = await _local.Accounts.ResolveAsync(agent, null, Context.ConnectionAborted);

        return Convert(account);
    }

    private static RemoteAccountResult Convert(ResolvedAccount account) =>
        new(account.AccountId,
            account.Name,
            account.HomePath,
            account.Fallback is null ? null : Convert(account.Fallback));

    public Task<IReadOnlyList<string>> GetBranches(string repositoryId) =>
        _local.GetBranchesAsync(repositoryId, Context.ConnectionAborted);

    public async Task<IReadOnlyList<RemoteWorkspaceFile>> ListWorkspaceFiles(string sessionId, int limit)
    {
        var files = await _local.Files.ListAsync(sessionId, limit, Context.ConnectionAborted);

        return files
            .Select(f => new RemoteWorkspaceFile(f.RelativePath, f.Name, f.SizeBytes, f.ModifiedAt, f.IsImage, f.IsText))
            .ToList();
    }

    public async Task<RemoteFileContent?> ReadWorkspaceFile(string sessionId, string relativePath)
    {
        var file = await _local.Files.OpenAsync(sessionId, relativePath, Context.ConnectionAborted);
        if (file is null)
            return null;

        await using var content = file.Value.Content;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, Context.ConnectionAborted);

        return new RemoteFileContent(file.Value.FileName, file.Value.ContentType, buffer.ToArray());
    }

    public async Task<string> StartTerminal(RemoteTerminalStart start)
    {
        await TouchAsync();

        var session = await _local.Terminals.StartAsync(
            start.FileName,
            start.Arguments,
            start.WorkingDirectory,
            start.Environment,
            start.PreferPseudoTerminal,
            Context.ConnectionAborted);

        _logger.LogInformation("Started a remote terminal running {Command}", start.FileName);

        return session.Id;
    }

    /// <summary>
    /// Follows a terminal's output. The whole buffer is sent each time rather than the delta: it is
    /// already capped by the terminal service, and a caller that reconnects mid-stream then gets a
    /// correct screen instead of the tail of one.
    /// </summary>
    public async IAsyncEnumerable<RemoteTerminalState> StreamTerminal(
        string id,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var session = _local.Terminals.Get(id);
        if (session is null)
            yield break;

        // A gate rather than a poll: the terminal raises Updated on every write, and this wakes on
        // it. Signalled once up front so the caller gets the state it has right now.
        var signal = new SemaphoreSlim(1, 1);
        void OnUpdated()
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Several writes between reads collapse into one send, which is what we want.
            }
        }

        session.Updated += OnUpdated;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await signal.WaitAsync(ct);

                // Snapshotted once and both sent and judged from that one reading. Reading
                // IsRunning again after sending would let a process that exited in between end the
                // stream right after a frame that said it was still going — leaving the caller with
                // a terminal that never reports its exit code and simply stops.
                var state = new RemoteTerminalState(
                    session.Id,
                    session.IsRunning,
                    session.ExitCode,
                    session.Buffer,
                    session.DetectedUrls,
                    session.DetectedCodes);

                yield return state;

                if (!state.IsRunning)
                    yield break;
            }
        }
        finally
        {
            session.Updated -= OnUpdated;
            signal.Dispose();
        }
    }

    public Task SendTerminal(string id, string input, bool appendNewline) =>
        _local.Terminals.Get(id)?.SendAsync(input, appendNewline, Context.ConnectionAborted) ?? Task.CompletedTask;

    public Task SendTerminalSecret(string id, string secret) =>
        _local.Terminals.Get(id)?.SendSecretAsync(secret, Context.ConnectionAborted) ?? Task.CompletedTask;

    public Task SendTerminalControl(string id, string letter) =>
        letter.Length == 0
            ? Task.CompletedTask
            : _local.Terminals.Get(id)?.SendControlAsync(letter[0], Context.ConnectionAborted) ?? Task.CompletedTask;

    public Task StopTerminal(string id) => _local.Terminals.CloseAsync(id);

    /// <summary>
    /// Notes that the grant behind this connection is in use, so the page listing them shows what is
    /// live rather than only what was once approved.
    /// </summary>
    private Task TouchAsync() =>
        Context.User?.FindFirst(RemoteTokenIssuer.GrantIdClaim)?.Value is { Length: > 0 } grantId
            ? _access.TouchAsync(grantId, Context.ConnectionAborted)
            : Task.CompletedTask;
}
