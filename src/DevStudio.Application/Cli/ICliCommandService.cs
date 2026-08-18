namespace DevStudio.Application.Cli;

public sealed record TerminalLine(string Text, bool IsError = false);

public sealed record TerminalResult(IReadOnlyList<TerminalLine> Lines, bool ClearScreen = false)
{
    public static TerminalResult Of(string text, bool isError = false) => new([new TerminalLine(text, isError)]);
    public static TerminalResult Of(IEnumerable<string> lines) => new(lines.Select(l => new TerminalLine(l)).ToList());
}

/// <summary>
/// Drives the app from a typed command line: <c>sessions list</c>, <c>agents run code-review "fix
/// the lint errors"</c>, <c>workflows run nightly-triage -b</c>. Long-running commands (starting a
/// session, running a workflow) block until finished by default; <c>-b</c> backgrounds them and
/// returns immediately with the id to track elsewhere in the app.
/// </summary>
public interface ICliCommandService
{
    Task<TerminalResult> ExecuteAsync(string commandLine, string invokedBy, CancellationToken ct = default);
}
