namespace DevStudio.Infrastructure.Terminals;

/// <summary>
/// A running command and the two streams that talk to it. There are two of these because there are
/// two ways to give a CLI a terminal: a real pseudo console (ConPTY on Windows, <c>script</c> on
/// Unix), or plain redirected pipes, which several of these CLIs refuse to log in over.
/// </summary>
internal interface ITerminalChannel : IDisposable
{
    /// <summary>Everything the command has written, as one stream. A pty merges stdout and stderr.</summary>
    IReadOnlyList<StreamReader> Readers { get; }

    bool IsRunning { get; }

    int? ExitCode { get; }

    /// <summary>
    /// True when the child really has a terminal. It decides how a token flow is ended: Ctrl-D on a
    /// pty, closing the pipe otherwise, and getting that wrong hangs the CLI waiting for input.
    /// </summary>
    bool IsPseudoTerminal { get; }

    event Action? Exited;

    Task WriteAsync(string text, CancellationToken ct = default);

    /// <summary>Ends the child's standard input. Only meaningful without a pty.</summary>
    void CloseInput();

    void Kill();
}
