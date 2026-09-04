using System.Collections.Concurrent;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Globals;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Remoting;

/// <summary>
/// Picks the machine a piece of work runs on. Null means this one, which is what almost every caller
/// passes and what everything did before remoting existed.
/// </summary>
public sealed class ExecutionHostResolver : IExecutionHostResolver
{
    private readonly IEntityStore<RemoteInstance> _instances;
    private readonly IRemoteConnectionPool _pool;
    private readonly IWorkspaceService _localWorkspaces;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISharedEnvironment _shared;
    private readonly ConcurrentDictionary<string, RemoteExecutionHost> _hosts = new();

    public ExecutionHostResolver(
        IExecutionHost local,
        IEntityStore<RemoteInstance> instances,
        IRemoteConnectionPool pool,
        IWorkspaceService localWorkspaces,
        ILoggerFactory loggerFactory,
        ISharedEnvironment shared)
    {
        Local = local;
        _instances = instances;
        _pool = pool;
        _localWorkspaces = localWorkspaces;
        _loggerFactory = loggerFactory;
        _shared = shared;

        // A host caches what the far side offers, so it has to be thrown away when the instance it
        // was built from changes — a re-pair, a new address, or being switched off.
        _instances.Changed += instance => _hosts.TryRemove(instance.Id, out _);
    }

    public IExecutionHost Local { get; }

    public async Task<IExecutionHost> ResolveAsync(string? remoteInstanceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remoteInstanceId))
            return Local;

        var instance = await _instances.GetAsync(remoteInstanceId, ct)
                       ?? throw new InvalidOperationException("That remote instance no longer exists.");

        if (!instance.Enabled)
            throw new InvalidOperationException($"'{instance.Name}' is switched off.");

        if (string.IsNullOrWhiteSpace(instance.AccessToken))
            throw new InvalidOperationException(
                $"'{instance.Name}' has not been paired. Request access from Remote instances, then approve it on {instance.Name}.");

        var host = _hosts.GetOrAdd(instance.Id, _ => new RemoteExecutionHost(
            instance,
            _pool,
            _localWorkspaces,
            _loggerFactory.CreateLogger<RemoteExecutionHost>(),
            _shared));

        // Its CLI registry is built from this, so nothing can resolve a CLI before the far side has
        // said what it has. Fetching here rather than at each use keeps that a single round trip.
        await host.GetConfigAsync(ct);

        return host;
    }

    public async Task<IReadOnlyList<RemoteInstance>> AvailableAsync(CancellationToken ct = default) =>
        (await _instances.GetAllAsync(ct))
        .Where(i => i.Enabled && !string.IsNullOrWhiteSpace(i.AccessToken))
        .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
