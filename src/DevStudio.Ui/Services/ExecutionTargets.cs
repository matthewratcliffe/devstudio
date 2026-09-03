using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;

namespace DevStudio.Ui.Services;

/// <summary>
/// What the pickers on a page are made of, for whichever machine is selected.
///
/// Both sides answer through the same call, so a page does not branch on local versus remote: it
/// asks for the config of the chosen target and fills its dropdowns from what comes back. That is
/// what makes choosing an instance change every dependent field at once, and it is why the local
/// path goes through here too — a code path only remote work takes is a code path only remote work
/// finds the bugs in.
/// </summary>
public sealed class ExecutionTargets
{
    private readonly IExecutionHostResolver _hosts;
    private readonly ILogger<ExecutionTargets> _logger;

    public ExecutionTargets(IExecutionHostResolver hosts, ILogger<ExecutionTargets> logger)
    {
        _hosts = hosts;
        _logger = logger;
    }

    /// <summary>Paired, enabled instances, for the "runs on" dropdown.</summary>
    public Task<IReadOnlyList<RemoteInstance>> InstancesAsync(CancellationToken ct = default) =>
        _hosts.AvailableAsync(ct);

    /// <summary>
    /// The chosen machine's CLIs, models, MCP servers, skills, checkouts and logins.
    ///
    /// A remote that cannot be reached comes back empty rather than throwing. The page is mid-render
    /// when this is called, and an exception there takes the circuit down; empty dropdowns plus the
    /// message in <paramref name="error"/> leave the operator able to pick something else.
    /// </summary>
    public async Task<(RemoteHostConfig Config, string? Error)> ConfigAsync(
        string? remoteInstanceId,
        CancellationToken ct = default)
    {
        try
        {
            var host = await _hosts.ResolveAsync(remoteInstanceId, ct);

            return (await host.GetConfigAsync(ct), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the configuration of remote instance {Instance}", remoteInstanceId);

            return (RemoteHostConfig.Empty("unreachable"), ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> BranchesAsync(
        string? remoteInstanceId,
        string repositoryId,
        CancellationToken ct = default)
    {
        try
        {
            var host = await _hosts.ResolveAsync(remoteInstanceId, ct);

            return await host.GetBranchesAsync(repositoryId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not list branches on remote instance {Instance}", remoteInstanceId);

            return [];
        }
    }
}
