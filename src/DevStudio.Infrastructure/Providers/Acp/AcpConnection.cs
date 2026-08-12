using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using DevStudio.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Providers.Acp;

/// <summary>
/// A duplex line channel to an ACP agent. Abstracted so the protocol can be exercised without
/// starting a process — the wire format is newline-delimited JSON either way.
/// </summary>
public interface IAcpConnection : IAsyncDisposable
{
    /// <summary>Lines the agent has written, in order, ending when it closes stdout.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);

    Task WriteLineAsync(string line, CancellationToken ct);
}

public interface IAcpConnectionFactory
{
    Task<IAcpConnection> ConnectAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken ct);
}

/// <summary>
/// Starts the agent and keeps its stdin open for the length of the conversation, which is what
/// separates this from <see cref="Processes.ProcessRunner"/> — that one runs a command and reads
/// what it prints, while ACP is a conversation in both directions.
/// </summary>
public sealed class AcpProcessConnectionFactory : IAcpConnectionFactory
{
    private readonly ILogger<AcpProcessConnectionFactory> _logger;

    public AcpProcessConnectionFactory(ILogger<AcpProcessConnectionFactory> logger) => _logger = logger;

    public Task<IAcpConnection> ConnectAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        foreach (var pair in environment)
            info.Environment[pair.Key] = pair.Value;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Start();

        // stderr is the agent's own logging, never protocol. Drained so a chatty agent cannot fill
        // the pipe and wedge itself.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
                    _logger.LogDebug("[acp:{Executable}] {Line}", executable, line);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ACP stderr ended");
            }
        }, CancellationToken.None);

        return Task.FromResult<IAcpConnection>(new ProcessConnection(process, _logger));
    }

    private sealed class ProcessConnection(Process process, ILogger logger) : IAcpConnection
    {
        public async IAsyncEnumerable<string> ReadLinesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (line is null)
                    yield break;

                if (line.Length > 0)
                    yield return line;
            }
        }

        public async Task WriteLineAsync(string line, CancellationToken ct)
        {
            await process.StandardInput.WriteAsync(line.AsMemory(), ct);
            await process.StandardInput.WriteAsync("\n".AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "ACP agent had already exited");
            }

            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>An in-memory connection, for tests and for driving a scripted agent.</summary>
public sealed class ScriptedAcpConnection : IAcpConnection
{
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

    public List<string> Written { get; } = [];

    /// <summary>Called with each line the client sends, so a fake agent can answer it.</summary>
    public Func<string, ScriptedAcpConnection, Task>? OnWrite { get; set; }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in _incoming.Reader.ReadAllAsync(ct))
            yield return line;
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        Written.Add(line);

        if (OnWrite is { } handler)
            await handler(line, this);
    }

    /// <summary>Queues a line as though the agent had written it.</summary>
    public void Reply(string line) => _incoming.Writer.TryWrite(line);

    public void Complete() => _incoming.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
