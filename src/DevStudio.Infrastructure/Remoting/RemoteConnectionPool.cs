using System.Collections.Concurrent;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Remoting;

/// <summary>
/// One live hub connection per paired instance, opened on first use and kept afterwards.
///
/// SignalR rather than plain HTTP because a turn is a stream: the events a CLI produces arrive over
/// a minute or several, and they have to appear in the transcript as they happen or a remote session
/// reads as a long silence followed by a wall of text. A hub method returning
/// <c>IAsyncEnumerable</c> maps onto <see cref="IProviderCli.RunTurnAsync"/> exactly, so the remote
/// path is the same shape as the local one rather than a translation of it.
/// </summary>
public interface IRemoteConnectionPool
{
    /// <summary>
    /// A connected hub for the instance, connecting if it is not already. Throws when the instance
    /// has no key or cannot be reached — the caller turns that into something the operator can read.
    /// </summary>
    Task<HubConnection> GetAsync(RemoteInstance instance, CancellationToken ct = default);

    /// <summary>Whether a connection is currently up, without opening one.</summary>
    bool IsConnected(string instanceId);

    /// <summary>
    /// Drops the connection for an instance. Called when its key or address changes, so the next use
    /// dials the new one rather than staying on a connection authenticated with the old key.
    /// </summary>
    Task DropAsync(string instanceId);

    /// <summary>Raised when a connection comes up or goes down, so the UI can show it.</summary>
    event Action<string, bool>? ConnectionChanged;
}

public sealed class RemoteConnectionPool : IRemoteConnectionPool, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ILogger<RemoteConnectionPool> _logger;

    public RemoteConnectionPool(ILogger<RemoteConnectionPool> logger)
    {
        _logger = logger;
    }

    public event Action<string, bool>? ConnectionChanged;

    public bool IsConnected(string instanceId) =>
        _entries.TryGetValue(instanceId, out var entry) &&
        entry.Connection.State == HubConnectionState.Connected;

    public async Task<HubConnection> GetAsync(RemoteInstance instance, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.AccessToken))
            throw new InvalidOperationException($"'{instance.Name}' has not been paired yet. Request access from Remote instances first.");

        var entry = _entries.GetOrAdd(instance.Id, _ => new Entry(Build(instance), instance.BaseUrl, instance.AccessToken));

        // The address or the key changing makes the held connection wrong rather than stale, so it
        // is rebuilt rather than reused. Comparing the token catches a re-pair after a revoke.
        if (entry.BaseUrl != instance.BaseUrl || entry.Token != instance.AccessToken)
        {
            await DropAsync(instance.Id);
            entry = _entries.GetOrAdd(instance.Id, _ => new Entry(Build(instance), instance.BaseUrl, instance.AccessToken));
        }

        // One dialler at a time. Several turns starting at once on a cold connection would otherwise
        // each open their own, and all but one would be abandoned mid-handshake.
        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.Connection.State == HubConnectionState.Connected)
                return entry.Connection;

            await entry.Connection.StartAsync(ct);
            _logger.LogInformation("Connected to remote instance {Instance} at {Url}", instance.Name, instance.BaseUrl);
            ConnectionChanged?.Invoke(instance.Id, true);

            return entry.Connection;
        }
        catch (Exception ex)
        {
            ConnectionChanged?.Invoke(instance.Id, false);
            throw new InvalidOperationException($"Could not reach '{instance.Name}' at {instance.BaseUrl}: {ex.Message}", ex);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task DropAsync(string instanceId)
    {
        if (!_entries.TryRemove(instanceId, out var entry))
            return;

        try
        {
            await entry.Connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the connection to {Instance} threw", instanceId);
        }

        ConnectionChanged?.Invoke(instanceId, false);
    }

    private HubConnection Build(RemoteInstance instance)
    {
        var url = $"{instance.BaseUrl.TrimEnd('/')}{RemoteHubMethods.Path}";

        var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                // Read on every (re)connect rather than captured, so a re-pair mid-life is picked up.
                options.AccessTokenProvider = () => Task.FromResult<string?>(instance.AccessToken);
            })
            // A dropped link should heal itself: this is a home network and the far machine sleeps.
            // A turn in flight when it drops is still lost — the CLI over there is gone with it —
            // but the next one does not need anybody to press anything.
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();

        // A turn can be quiet for a long time while a CLI thinks, so the default 30 seconds would
        // tear down connections that are working perfectly well.
        connection.ServerTimeout = TimeSpan.FromMinutes(5);
        connection.HandshakeTimeout = TimeSpan.FromSeconds(30);

        connection.Reconnected += _ =>
        {
            ConnectionChanged?.Invoke(instance.Id, true);
            return Task.CompletedTask;
        };

        connection.Closed += _ =>
        {
            ConnectionChanged?.Invoke(instance.Id, false);
            return Task.CompletedTask;
        };

        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _entries.Keys.ToList())
            await DropAsync(id);
    }

    private sealed class Entry(HubConnection connection, string baseUrl, string? token)
    {
        public HubConnection Connection { get; } = connection;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string BaseUrl { get; } = baseUrl;
        public string? Token { get; } = token;
    }
}
