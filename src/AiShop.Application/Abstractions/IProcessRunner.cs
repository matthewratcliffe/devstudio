namespace AiShop.Application.Abstractions;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? StandardInput = null,
    int TimeoutSeconds = 120);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>stdout when the process succeeded, otherwise whichever stream carried the message.</summary>
    public string Text => Succeeded
        ? StandardOutput.Trim()
        : (string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError).Trim();
}

/// <summary>Runs a child process to completion and collects its output.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default);

    /// <summary>Streams stdout+stderr lines as they arrive; the task result is the exit code.</summary>
    Task<int> StreamAsync(
        ProcessRequest request,
        Func<string, bool, CancellationToken, Task> onLine,
        CancellationToken ct = default);
}
