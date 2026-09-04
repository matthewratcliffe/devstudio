using System.Diagnostics;
using DevStudio.Application.Common;
using DevStudio.Application.Globals;
using DevStudio.Infrastructure.Terminals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Providers;

/// <summary>Makes sure a healthy opencode server is listening before a turn tries to talk to it.</summary>
public interface IOpencodeServerManager
{
    Task EnsureRunningAsync(CancellationToken ct);
}

/// <summary>
/// Starts and supervises DevStudio's own <c>opencode serve</c> process rather than assuming an
/// operator already has one running: opencode is a plain CLI with no daemon manager of its own, and
/// every other provider here is driven directly, so opencode needing a separate terminal kept open
/// first would be the odd one out. When <see cref="OrchestratorOptions.OpencodeBaseUrl"/> is already
/// answering — an operator's own server, or one from an earlier run this process attached to —
/// nothing is started; a server is only ever launched when the door is not already open.
/// </summary>
public sealed class OpencodeServerManager : IOpencodeServerManager, IAsyncDisposable
{
    private readonly IHttpClientFactory _clients;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<OpencodeServerManager> _logger;
    private readonly ISharedEnvironment _shared;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;

    public OpencodeServerManager(
        IHttpClientFactory clients,
        IOptions<OrchestratorOptions> options,
        ILogger<OpencodeServerManager> logger,
        ISharedEnvironment shared)
    {
        _clients = clients;
        _options = options.Value;
        _logger = logger;
        _shared = shared;
    }

    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (!_options.OpencodeAutoStart || string.IsNullOrWhiteSpace(_options.OpencodeBaseUrl))
            return;

        if (await IsHealthyAsync(ct))
            return;

        await _lock.WaitAsync(ct);
        try
        {
            // Another turn may have already brought the server up while this one waited its turn
            // for the lock.
            if (await IsHealthyAsync(ct))
                return;

            if (_process is null || _process.HasExited)
                Start(await _shared.ForLocalAsync(ct));

            await WaitUntilHealthyAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Unlike the per-turn adapters this is one long-lived process, so the shared variables it gets
    /// are the ones in force when it starts. Changing them takes effect for opencode only once the
    /// server is restarted — which is what the Settings page warns about.
    /// </summary>
    private void Start(IReadOnlyDictionary<string, string> shared)
    {
        if (!Uri.TryCreate(_options.OpencodeBaseUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException($"'{_options.OpencodeBaseUrl}' is not a valid opencode server URL.");

        List<string> arguments = ["serve", "--hostname", baseUri.Host, "--port", baseUri.Port.ToString()];

        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // npm installs opencode on Windows as a .cmd shim, not a plain .exe. CreateProcess has no
        // idea PATHEXT exists, so a bare "opencode" resolves to nothing even though the same name
        // runs fine from a shell — the same problem the terminal CLIs hit, and the same fix.
        if (OperatingSystem.IsWindows())
        {
            var executable = WindowsCommandLine.Resolve(_options.OpencodeExecutable);

            if (WindowsCommandLine.NeedsCommandInterpreter(executable))
            {
                // Arguments (one raw string), not ArgumentList: an ArgumentList entry gets
                // Win32-quoted again on its way into the real command line, and since this string
                // already contains its own literal quotes, that second pass turns them into \" and
                // cmd fails to recognise the command at all.
                info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                var inner = string.Join(" ",
                    new[] { $"\"{executable}\"" }.Concat(arguments.Select(WindowsCommandLine.Quote)));
                info.Arguments = $"/d /s /c \"{inner}\"";
            }
            else
            {
                info.FileName = executable;
                foreach (var argument in arguments)
                    info.ArgumentList.Add(argument);
            }
        }
        else
        {
            info.FileName = _options.OpencodeExecutable;
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
        }

        _logger.LogInformation("Starting opencode server: {Executable} serve --hostname {Host} --port {Port}",
            _options.OpencodeExecutable, baseUri.Host, baseUri.Port);

        foreach (var pair in shared)
            info.Environment[pair.Key] = pair.Value;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // The OS's own message ("cannot find the file specified") is accurate but tells nobody
            // what to do about it — this is almost always opencode simply not being installed, or
            // installed somewhere not on PATH.
            throw new InvalidOperationException(
                $"Could not start '{_options.OpencodeExecutable}'. Install opencode and make sure it is " +
                "on PATH, point Orchestrator__OpencodeExecutable at its full path, or turn off " +
                "Orchestrator__OpencodeAutoStart and run `opencode serve` yourself at " +
                $"{_options.OpencodeBaseUrl}.", ex);
        }

        // Neither stream is protocol here — just the server's own log — but they still have to be
        // drained or a chatty server fills the pipe and stalls.
        _ = DrainAsync(process.StandardOutput, "stdout");
        _ = DrainAsync(process.StandardError, "stderr");

        _process = process;
    }

    private async Task DrainAsync(StreamReader reader, string streamName)
    {
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
                _logger.LogDebug("[opencode:{Stream}] {Line}", streamName, line);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "opencode {Stream} ended", streamName);
        }
    }

    /// <summary>Polls until the server answers or it gives up — whichever comes first.</summary>
    private async Task WaitUntilHealthyAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException($"opencode serve exited immediately with code {_process.ExitCode}.");

            if (await IsHealthyAsync(ct))
                return;

            await Task.Delay(250, ct);
        }

        throw new InvalidOperationException($"opencode did not start listening on {_options.OpencodeBaseUrl} within 30 seconds.");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var client = _clients.CreateClient(nameof(OpencodeServerManager));
            client.BaseAddress = new Uri(_options.OpencodeBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync("doc", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not { HasExited: false } process)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stop the opencode server cleanly");
        }
        finally
        {
            process.Dispose();
        }
    }
}
