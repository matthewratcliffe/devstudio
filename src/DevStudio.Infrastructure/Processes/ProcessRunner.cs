using System.Diagnostics;
using System.Text;
using DevStudio.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Processes;

/// <summary>Runs child processes with redirected stdio. Every CLI in this app goes through here.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger) => _logger = logger;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default)
    {
        using var process = Create(request);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start {FileName}", request.FileName);
            return new ProcessResult(-1, string.Empty, $"Could not start '{request.FileName}': {ex.Message}", false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await CloseStandardInputAsync(process, request);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.TimeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: !ct.IsCancellationRequested);
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    public async Task<int> StreamAsync(
        ProcessRequest request,
        Func<string, bool, CancellationToken, Task> onLine,
        CancellationToken ct = default)
    {
        using var process = Create(request);
        process.Start();

        // Read both pipes concurrently so a chatty stderr cannot deadlock stdout.
        var stdoutTask = PumpAsync(process.StandardOutput, false, onLine, ct);
        var stderrTask = PumpAsync(process.StandardError, true, onLine, ct);

        await CloseStandardInputAsync(process, request);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Sends whatever input the request carries and then closes the pipe. Closing it matters even
    /// when there is nothing to send: stdin is always redirected, and a CLI that checks whether it
    /// is piped — codex reads it as extra instructions — waits on an end that never comes.
    /// </summary>
    private static async Task CloseStandardInputAsync(Process process, ProcessRequest request)
    {
        try
        {
            if (request.StandardInput is not null)
                await process.StandardInput.WriteAsync(request.StandardInput);

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // A process that exited immediately takes its stdin with it.
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        bool isError,
        Func<string, bool, CancellationToken, Task> onLine,
        CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
                await onLine(line, isError, ct);
        }
        catch (OperationCanceledException)
        {
            // The caller is shutting the process down; nothing to report.
        }
    }

    private static Process Create(ProcessRequest request)
    {
        var executable = ResolveExecutable(request.FileName);
        var info = new ProcessStartInfo
        {
            FileName = executable.FileName,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (executable.IsWindowsScript)
        {
            // npm installs CLI entry points as .cmd shims on Windows. They are resolved by a
            // shell, but cannot be launched directly by CreateProcess with UseShellExecute=false.
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/s");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(string.Join(" ",
                new[] { QuoteCommandArgument(executable.FileName) }
                    .Concat(request.Arguments.Select(QuoteCommandArgument))));
        }
        else
        {
            foreach (var argument in request.Arguments)
                info.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
                info.Environment[pair.Key] = pair.Value;
        }

        return new Process { StartInfo = info, EnableRaisingEvents = true };
    }

    private static (string FileName, bool IsWindowsScript) ResolveExecutable(string fileName)
    {
        if (!OperatingSystem.IsWindows() || Path.IsPathFullyQualified(fileName) || fileName.Contains(Path.DirectorySeparatorChar))
            return (fileName, IsWindowsScript(fileName));

        // The bare name comes last, never first: npm writes both a POSIX shim ("claude", a shell
        // script CreateProcess cannot launch) and a Windows one ("claude.cmd") into the same
        // directory, and picking the extensionless file fails with "not a valid application for
        // this OS platform". An explicit extension is used exactly as given.
        var extensions = Path.HasExtension(fileName)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append(string.Empty);
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(directory.Trim('"'), fileName + extension);
            if (File.Exists(candidate))
                return (candidate, IsWindowsScript(candidate));
        }

        return (fileName, IsWindowsScript(fileName));
    }

    private static bool IsWindowsScript(string fileName) =>
        OperatingSystem.IsWindows() &&
        (fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
         fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

    private static string QuoteCommandArgument(string argument)
    {
        if (argument.Length == 0)
            return "\"\"";

        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
            return argument;

        return $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process already went away.
        }
    }
}
