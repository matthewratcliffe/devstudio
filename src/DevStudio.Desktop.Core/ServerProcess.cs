using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DevStudio.Desktop;

/// <summary>
/// Runs the web app as a child process and keeps it tied to this one. It is the same executable the
/// container runs — a shell adds a window, not a second implementation.
/// </summary>
public sealed class ServerProcess : IDisposable
{
    private const int PreferredPort = 7080;
    private const int LogLines = 400;

    private readonly Queue<string> _log = new();
    private readonly Lock _gate = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly IChildLifetime _lifetime = ChildLifetime.Create();

    private Process? _process;

    public int Port { get; private set; }

    public string Url => $"http://127.0.0.1:{Port}/";

    public string Log
    {
        get { lock (_gate) return string.Join(Environment.NewLine, _log); }
    }

    public bool HasExited => _process is null or { HasExited: true };

    public void Start()
    {
        var executable = Locate();
        Port = ChoosePort();

        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var pair in DesktopPaths.ServerEnvironment(Port))
            info.Environment[pair.Key] = pair.Value;

        Directory.CreateDirectory(DesktopPaths.DataRoot);

        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Without this the server outlives a crash of the shell and holds the port, so the next
        // launch fails with no window and no explanation.
        _lifetime.Adopt(_process);
    }

    /// <summary>Waits for the health endpoint. Startup failures land in the log the UI can show.</summary>
    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_process is { HasExited: true })
                return false;

            try
            {
                using var response = await _http.GetAsync($"http://127.0.0.1:{Port}/healthz", ct);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (Exception)
            {
                // Not listening yet.
            }

            await Task.Delay(200, ct);
        }

        return false;
    }

    /// <summary>The last few lines of the child's output, which is where a startup failure explains itself.</summary>
    public string Tail(int lines = 25)
    {
        var all = Log.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        return all.Length <= lines ? Log : string.Join(Environment.NewLine, all[^lines..]);
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or taken down by the job object first.
        }

        _process?.Dispose();
        _http.Dispose();
        _lifetime.Dispose();
    }

    private void Append(string? line)
    {
        if (line is null)
            return;

        lock (_gate)
        {
            _log.Enqueue(line);
            while (_log.Count > LogLines)
                _log.Dequeue();
        }

        try
        {
            File.AppendAllText(DesktopPaths.LogFile, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // The in-memory log is what the UI shows; the file is a convenience.
        }
    }

    /// <summary>
    /// 7080 when it is free, so a bookmark keeps working and the documented port stays right.
    /// Anything else free otherwise, rather than refusing to start.
    /// </summary>
    private static int ChoosePort()
    {
        if (IsFree(PreferredPort))
            return PreferredPort;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// The managed assembly that belongs to an apphost. Not ChangeExtension: the Unix apphost is
    /// named "DevStudio.Ui" with no extension at all, and ChangeExtension would replace the ".Ui".
    /// </summary>
    private static string ManagedAssemblyFor(string apphost) =>
        OperatingSystem.IsWindows() ? Path.ChangeExtension(apphost, ".dll") : apphost + ".dll";

    /// <summary>Finds the server beside the shell when installed, and in the build output when not.</summary>
    public static string Locate()
    {
        var name = OperatingSystem.IsWindows() ? "DevStudio.Ui.exe" : "DevStudio.Ui";
        var candidates = new List<string>();

        if (Environment.GetEnvironmentVariable("DEVSTUDIO_SERVER") is { Length: > 0 } configured)
            candidates.Add(configured);

        // Installed layout, the same on all three platforms: the shell at the root of the package,
        // the server one directory down. On macOS that root is Contents/MacOS inside the .app.
        var here = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(here, "server", name));
        candidates.Add(Path.Combine(here, name));

        // Running from the build output: walk up to the repository and use the sibling project.
        for (var directory = new DirectoryInfo(here); directory is not null; directory = directory.Parent)
        {
            var output = Path.Combine(directory.FullName, "src", "DevStudio.Ui", "bin");
            if (!Directory.Exists(output))
                continue;

            candidates.AddRange(Directory
                .EnumerateFiles(output, name, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc));

            break;
        }

        foreach (var candidate in candidates)
        {
            // The apphost on its own is not enough: a stray copy of it without its managed
            // assemblies beside it starts, fails to find its .dll, and exits before it listens.
            if (File.Exists(candidate) && File.Exists(ManagedAssemblyFor(candidate)))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Could not find {name}. Looked beside this program, in ./server, and in the build " +
            "output. Set DEVSTUDIO_SERVER to point at it.");
    }
}
