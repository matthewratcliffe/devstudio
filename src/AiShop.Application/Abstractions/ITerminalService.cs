namespace AiShop.Application.Abstractions;

/// <summary>
/// An interactive command attached to a pseudo-terminal, so browser users can complete the
/// device-code login flows that <c>claude</c>, <c>codex</c> and <c>gh</c> only offer interactively.
/// </summary>
public interface ITerminalSession : IAsyncDisposable
{
    string Id { get; }
    bool IsRunning { get; }
    int? ExitCode { get; }

    /// <summary>Everything the process has printed so far, with ANSI control sequences removed.</summary>
    string Buffer { get; }

    /// <summary>URLs seen in the output — the login link the user has to open.</summary>
    IReadOnlyList<string> DetectedUrls { get; }

    /// <summary>One-time codes seen in the output, for the device-code login flows.</summary>
    IReadOnlyList<string> DetectedCodes { get; }

    /// <summary>Fired whenever the buffer grows or the process exits.</summary>
    event Action? Updated;

    /// <summary>Writes to the process stdin. A newline is appended unless suppressed.</summary>
    Task SendAsync(string input, bool appendNewline = true, CancellationToken ct = default);

    /// <summary>
    /// Writes a secret to the process without putting it in the visible buffer, for the flows that
    /// read a key or token from stdin.
    /// </summary>
    Task SendSecretAsync(string secret, CancellationToken ct = default);

    Task SendControlAsync(char letter, CancellationToken ct = default);

    Task StopAsync();
}

public interface ITerminalService
{
    /// <summary>Starts a terminal-attached command and tracks it until it is disposed.</summary>
    Task<ITerminalSession> StartAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default);

    ITerminalSession? Get(string id);
    IReadOnlyList<ITerminalSession> Active { get; }
    Task CloseAsync(string id);
}
