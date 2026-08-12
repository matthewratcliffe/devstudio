using System.Diagnostics;
using System.Text;

namespace DevStudio.Infrastructure.Terminals;

/// <summary>
/// A command on redirected pipes. On Unix it is wrapped in <c>script</c>, which allocates a real pty
/// — the container's usual path. On Windows it is the fallback for a machine too old for ConPTY,
/// where the CLIs can be driven but interactive logins are limited.
/// </summary>
internal sealed class ProcessTerminalChannel : ITerminalChannel
{
    private readonly Process _process;

    public ProcessTerminalChannel(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (OperatingSystem.IsWindows())
        {
            info.FileName = fileName;

            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
        }
        else
        {
            // script -q -e -c "<command>" /dev/null gives the child a pty and preserves its exit code.
            info.FileName = "script";
            info.ArgumentList.Add("-q");
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(ShellCommand(fileName, arguments));
            info.ArgumentList.Add("/dev/null");
        }

        foreach (var pair in environment)
            info.Environment[pair.Key] = pair.Value;

        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.Exited += (_, _) => Exited?.Invoke();
    }

    /// <summary>Unix gets a pty from <c>script</c>; Windows here does not, which is the whole reason ConPTY exists.</summary>
    public bool IsPseudoTerminal => !OperatingSystem.IsWindows();

    public IReadOnlyList<StreamReader> Readers => [_process.StandardOutput, _process.StandardError];

    public bool IsRunning => !_process.HasExited;

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public event Action? Exited;

    public void Start() => _process.Start();

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        await _process.StandardInput.WriteAsync(text.AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }

    public void CloseInput() => _process.StandardInput.Close();

    public void Kill()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
    }

    public void Dispose()
    {
        try
        {
            Kill();
        }
        catch (Exception)
        {
            // Already gone.
        }

        _process.Dispose();
    }

    private static string ShellCommand(string fileName, IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Select(Quote).Prepend(Quote(fileName)));

    private static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '/' or '.' or '=')
            ? value
            : "'" + value.Replace("'", "'\\''") + "'";
}
