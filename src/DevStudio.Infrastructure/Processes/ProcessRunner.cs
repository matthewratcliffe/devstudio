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

        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput);
            process.StandardInput.Close();
        }

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

        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput);
            process.StandardInput.Close();
        }

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
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in request.Arguments)
            info.ArgumentList.Add(argument);

        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
                info.Environment[pair.Key] = pair.Value;
        }

        return new Process { StartInfo = info, EnableRaisingEvents = true };
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
