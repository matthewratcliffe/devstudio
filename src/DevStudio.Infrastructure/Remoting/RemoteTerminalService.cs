using System.Collections.Concurrent;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Remoting;
using DevStudio.Domain.Remoting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Remoting;

/// <summary>
/// A terminal running on another machine. This is the plainest form of what the whole feature is
/// for: a command typed here, executed there, with the output arriving as it is printed.
///
/// The buffer is mirrored rather than fetched on demand. The far side streams its state whenever it
/// changes, which is what makes a remote login flow — where the thing to catch is a code that
/// appears for a minute — usable at all.
/// </summary>
public sealed class RemoteTerminalService : ITerminalService
{
    private readonly RemoteInstance _instance;
    private readonly IRemoteConnectionPool _pool;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, RemoteTerminalSession> _sessions = new();

    public RemoteTerminalService(RemoteInstance instance, IRemoteConnectionPool pool, ILogger logger)
    {
        _instance = instance;
        _pool = pool;
        _logger = logger;
    }

    public IReadOnlyList<ITerminalSession> Active =>
        _sessions.Values.Where(s => s.IsRunning).Cast<ITerminalSession>().ToList();

    public ITerminalSession? Get(string id) => _sessions.GetValueOrDefault(id);

    public async Task<ITerminalSession> StartAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool preferPseudoTerminal = true,
        CancellationToken ct = default)
    {
        var connection = await _pool.GetAsync(_instance, ct);

        var id = await connection.InvokeAsync<string>(
            RemoteHubMethods.StartTerminal,
            new RemoteTerminalStart(fileName, arguments, workingDirectory, environment, preferPseudoTerminal),
            ct);

        var session = new RemoteTerminalSession(id, connection, _logger);
        _sessions[id] = session;
        session.BeginMirroring();

        return session;
    }

    public async Task CloseAsync(string id)
    {
        if (_sessions.TryRemove(id, out var session))
            await session.DisposeAsync();
    }
}

/// <summary>
/// The local half of a terminal on the far side. Its buffer is whatever the last streamed state
/// said, and every keystroke is a call back over the hub.
/// </summary>
internal sealed class RemoteTerminalSession : ITerminalSession
{
    private readonly HubConnection _connection;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _mirror;

    public RemoteTerminalSession(string id, HubConnection connection, ILogger logger)
    {
        Id = id;
        _connection = connection;
        _logger = logger;
    }

    public string Id { get; }
    public bool IsRunning { get; private set; } = true;
    public int? ExitCode { get; private set; }
    public string Buffer { get; private set; } = string.Empty;
    public IReadOnlyList<string> DetectedUrls { get; private set; } = [];
    public IReadOnlyList<string> DetectedCodes { get; private set; } = [];

    public event Action? Updated;

    /// <summary>
    /// Starts following the far side's state. Deliberately not awaited: the caller wants the session
    /// back now and the output as it arrives, which is the same contract the local terminal has.
    /// </summary>
    public void BeginMirroring()
    {
        _mirror = Task.Run(async () =>
        {
            try
            {
                var stream = _connection.StreamAsync<RemoteTerminalState>(
                    RemoteHubMethods.StreamTerminal,
                    Id,
                    _cts.Token);

                await foreach (var state in stream.WithCancellation(_cts.Token))
                {
                    Buffer = state.Buffer;
                    IsRunning = state.IsRunning;
                    ExitCode = state.ExitCode;
                    DetectedUrls = state.DetectedUrls;
                    DetectedCodes = state.DetectedCodes;

                    Updated?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The process over there is gone, or the link is. Either way the terminal is over,
                // and saying so beats a window that looks live and never prints again.
                _logger.LogWarning(ex, "Lost the mirror of remote terminal {Id}", Id);
                IsRunning = false;
                Updated?.Invoke();
            }
        });
    }

    public Task SendAsync(string input, bool appendNewline = true, CancellationToken ct = default) =>
        _connection.InvokeAsync(RemoteHubMethods.SendTerminal, Id, input, appendNewline, ct);

    public Task SendSecretAsync(string secret, CancellationToken ct = default) =>
        _connection.InvokeAsync(RemoteHubMethods.SendTerminalSecret, Id, secret, ct);

    public Task SendControlAsync(char letter, CancellationToken ct = default) =>
        _connection.InvokeAsync(RemoteHubMethods.SendTerminalControl, Id, letter.ToString(), ct);

    public async Task StopAsync()
    {
        try
        {
            await _connection.InvokeAsync(RemoteHubMethods.StopTerminal, Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping remote terminal {Id} threw", Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
