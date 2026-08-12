using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiShop.Infrastructure.Terminals;

/// <summary>
/// Runs a command attached to a terminal so the browser can drive interactive logins.
/// On Linux the command is wrapped in <c>script</c>, which allocates a real pty — without one the
/// CLIs detect a pipe and refuse to start their device-code flow.
/// </summary>
public sealed partial class TerminalService : ITerminalService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    private readonly OrchestratorOptions _options;
    private readonly ILogger<TerminalService> _logger;

    public TerminalService(IOptions<OrchestratorOptions> options, ILogger<TerminalService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyList<ITerminalSession> Active => _sessions.Values.Cast<ITerminalSession>().ToList();

    public Task<ITerminalSession> StartAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var session = new TerminalSession(fileName, arguments, workingDirectory ?? _options.HomePath, BuildEnvironment(environment), _logger);
        session.Start();
        _sessions[session.Id] = session;
        return Task.FromResult<ITerminalSession>(session);
    }

    public ITerminalSession? Get(string id) => _sessions.TryGetValue(id, out var session) ? session : null;

    public async Task CloseAsync(string id)
    {
        if (_sessions.TryRemove(id, out var session))
            await session.DisposeAsync();
    }

    private Dictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string>? extra)
    {
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = _options.HomePath,
            ["CODEX_HOME"] = Path.Combine(_options.HomePath, ".codex"),
            ["TERM"] = "xterm-256color",
            // Without a size the CLIs wrap at 80 columns or refuse to draw at all.
            ["COLUMNS"] = "120",
            ["LINES"] = "34",
            // The CLIs try to open a browser on the container, which nobody can see.
            ["BROWSER"] = "echo",
            ["NO_COLOR"] = "1",
        };

        if (extra is not null)
        {
            foreach (var pair in extra)
                environment[pair.Key] = pair.Value;
        }

        return environment;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync();

        _sessions.Clear();
    }

    private sealed partial class TerminalSession : ITerminalSession
    {
        private const int MaxBufferChars = 200_000;

        /// <summary>How much of the tail to re-scan for links and codes on each read.</summary>
        private const int ScanWindowChars = 4_000;

        /// <summary>The window size reported to any CLI that asks. Matches COLUMNS and LINES.</summary>
        private const int Columns = 120;
        private const int Rows = 34;

        private readonly StringBuilder _buffer = new();
        private readonly List<string> _urls = [];
        private readonly List<string> _codes = [];
        private readonly Process _process;
        private readonly ILogger _logger;
        private readonly object _gate = new();

        public TerminalSession(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            ILogger logger)
        {
            _logger = logger;
            Id = Guid.NewGuid().ToString("n");
            Command = $"{fileName} {string.Join(' ', arguments)}".Trim();

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
                // No pty here; enough for development, and the container is where logins really happen.
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
                info.ArgumentList.Add(BuildShellCommand(fileName, arguments));
                info.ArgumentList.Add("/dev/null");
            }

            foreach (var pair in environment)
                info.Environment[pair.Key] = pair.Value;

            _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        }

        public string Id { get; }
        public string Command { get; }
        public bool IsRunning => !_process.HasExited;
        public int? ExitCode { get; private set; }

        public string Buffer
        {
            get { lock (_gate) return _buffer.ToString(); }
        }

        public IReadOnlyList<string> DetectedUrls
        {
            get { lock (_gate) return _urls.ToList(); }
        }

        public IReadOnlyList<string> DetectedCodes
        {
            get { lock (_gate) return _codes.ToList(); }
        }

        public event Action? Updated;

        public void Start()
        {
            _process.Exited += (_, _) =>
            {
                ExitCode = SafeExitCode();
                Append($"\n[process exited with code {ExitCode}]\n");
            };

            try
            {
                _process.Start();

                // Read characters rather than lines. Every one of these CLIs ends its login flow on a
                // prompt with no trailing newline ("Paste code here >"), which a line reader never
                // surfaces — that is what left the terminal window blank.
                _ = Task.Run(() => PumpAsync(_process.StandardOutput));
                _ = Task.Run(() => PumpAsync(_process.StandardError));
            }
            catch (Exception ex)
            {
                Append($"Could not start '{Command}': {ex.Message}\n");
                _logger.LogWarning(ex, "Terminal session failed to start");
            }
        }

        private async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[1024];

            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0)
                        break;

                    Append(new string(buffer, 0, read));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Terminal {Id} output stream ended", Id);
            }
        }

        public async Task SendAsync(string input, bool appendNewline = true, CancellationToken ct = default)
        {
            if (!IsRunning)
                return;

            try
            {
                await _process.StandardInput.WriteAsync(input.AsMemory(), ct);
                if (appendNewline)
                    await _process.StandardInput.WriteAsync("\n".AsMemory(), ct);

                await _process.StandardInput.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not write to terminal {Id}", Id);
            }
        }

        public async Task SendSecretAsync(string secret, CancellationToken ct = default)
        {
            if (!IsRunning || string.IsNullOrEmpty(secret))
                return;

            // The value goes to the process, never to the transcript everyone can read.
            await SendAsync(secret, appendNewline: true, ct);
            Append($"[sent {secret.Length} characters]{Environment.NewLine}");
        }

        public Task SendControlAsync(char letter, CancellationToken ct = default)
        {
            var code = (char)(char.ToUpperInvariant(letter) - 'A' + 1);
            return SendAsync(code.ToString(), appendNewline: false, ct);
        }

        public Task StopAsync()
        {
            TryKill();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Answers the queries a terminal UI expects a real terminal to answer. Bubbletea and survey
        /// based CLIs — gh among them — ask for the cursor position and colours on start-up and block
        /// until something replies, which is why a keypress appeared to do nothing. The size reported
        /// here is the size claimed through COLUMNS and LINES.
        /// </summary>
        private void AnswerTerminalQueries(string raw)
        {
            if (!raw.Contains('\u001b'))
                return;

            var replies = new StringBuilder();

            // Cursor position report — also how a UI discovers the window size, by parking the cursor
            // at 999;999 and asking where it ended up.
            if (raw.Contains("\u001b[6n", StringComparison.Ordinal))
                replies.Append($"\u001b[{Rows};{Columns}R");

            // Primary and secondary device attributes.
            if (raw.Contains("\u001b[c", StringComparison.Ordinal) || raw.Contains("\u001b[0c", StringComparison.Ordinal))
                replies.Append("\u001b[?1;2c");

            if (raw.Contains("\u001b[>c", StringComparison.Ordinal) || raw.Contains("\u001b[>0c", StringComparison.Ordinal))
                replies.Append("\u001b[>0;10;1c");

            // Foreground and background colour queries, answered with this app's own palette.
            if (raw.Contains("\u001b]10;?", StringComparison.Ordinal))
                replies.Append("\u001b]10;rgb:eeee/f2f2/ffff\u001b\\");

            if (raw.Contains("\u001b]11;?", StringComparison.Ordinal))
                replies.Append("\u001b]11;rgb:0505/0707/0f0f\u001b\\");

            // XTVERSION.
            if (raw.Contains("\u001b[>0q", StringComparison.Ordinal) || raw.Contains("\u001b[>q", StringComparison.Ordinal))
                replies.Append("\u001bP>|ai-shop\u001b\\");

            if (replies.Length == 0)
                return;

            _ = SendAsync(replies.ToString(), appendNewline: false);
        }

        private void Append(string text)
        {
            AnswerTerminalQueries(text);

            var clean = AnsiPattern().Replace(text, string.Empty).Replace("\r", string.Empty);

            lock (_gate)
            {
                _buffer.Append(clean);
                if (_buffer.Length > MaxBufferChars)
                    _buffer.Remove(0, _buffer.Length - MaxBufferChars);

                // Scan the recent buffer rather than this chunk alone: a pty hands over whatever has
                // been flushed, so a URL or a code is regularly split across two reads.
                var window = _buffer.Length <= ScanWindowChars
                    ? _buffer.ToString()
                    : _buffer.ToString(_buffer.Length - ScanWindowChars, ScanWindowChars);

                foreach (Match match in UrlPattern().Matches(window))
                {
                    var url = match.Value.TrimEnd('.', ',', ')', ']');
                    if (!_urls.Contains(url))
                        _urls.Add(url);
                }

                // Device-code logins print a code to type into the browser; it is the step people
                // miss, so it gets pulled out of the scrollback.
                foreach (Match match in DeviceCodePattern().Matches(window))
                {
                    if (!_codes.Contains(match.Value))
                        _codes.Add(match.Value);
                }
            }

            Updated?.Invoke();
        }

        private static string BuildShellCommand(string fileName, IReadOnlyList<string> arguments)
        {
            var parts = new List<string> { Quote(fileName) };
            parts.AddRange(arguments.Select(Quote));
            return string.Join(' ', parts);
        }

        private static string Quote(string value) =>
            value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '/' or '.' or '=')
                ? value
                : "'" + value.Replace("'", "'\\''") + "'";

        private int? SafeExitCode()
        {
            try { return _process.ExitCode; }
            catch { return null; }
        }

        private void TryKill()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
        }

        public ValueTask DisposeAsync()
        {
            TryKill();
            _process.Dispose();
            return ValueTask.CompletedTask;
        }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[()#][0-9A-Za-z]|\x1B.", RegexOptions.Singleline)]
        private static partial Regex AnsiPattern();

        [GeneratedRegex(@"https?://[^\s""'<>]+")]
        private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4,5}\b")]
        private static partial Regex DeviceCodePattern();
    }
}
