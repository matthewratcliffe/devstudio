using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DevStudio.Infrastructure.Terminals;

/// <summary>
/// A real terminal on Windows, through ConPTY.
///
/// Without it the CLIs see a pipe and behave like it: gh and codex refuse to start a device-code
/// flow, prompts that expect a keypress never appear, and anything drawn with a full-screen UI comes
/// out as a wall of escape codes or nothing at all. ConPTY is what a terminal emulator uses, and it
/// is what the Unix side already had through <c>script</c>.
///
/// Available since Windows 10 1809. Where it is missing, creation fails and the caller falls back to
/// redirected pipes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ConPtyTerminalChannel : ITerminalChannel
{
    private readonly nint _pseudoConsole;
    private readonly nint _processHandle;
    private readonly nint _threadHandle;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;

    private bool _disposed;

    private ConPtyTerminalChannel(
        nint pseudoConsole,
        nint processHandle,
        nint threadHandle,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead)
    {
        _pseudoConsole = pseudoConsole;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        _inputWrite = inputWrite;
        _outputRead = outputRead;

        // The console speaks UTF-8 both ways, and nothing here should wait for a full buffer before
        // a prompt reaches the browser.
        _writer = new StreamWriter(new FileStream(_inputWrite, FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
        _reader = new StreamReader(new FileStream(_outputRead, FileAccess.Read), new UTF8Encoding(false));

        WatchForExit();
    }

    public IReadOnlyList<StreamReader> Readers => [_reader];

    public bool IsPseudoTerminal => true;

    public bool IsRunning => ExitCode is null;

    public int? ExitCode { get; private set; }

    public event Action? Exited;

    /// <summary>
    /// Starts a command with a pseudo console attached. Returns null when ConPTY is unavailable or
    /// the process cannot be created, which is the caller's cue to fall back to pipes.
    /// </summary>
    public static ConPtyTerminalChannel? TryStart(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns,
        short rows)
    {
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        var pseudoConsole = nint.Zero;
        var attributes = nint.Zero;

        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, nint.Zero, 0) ||
                !CreatePipe(out outputRead, out outputWrite, nint.Zero, 0))
                return null;

            if (CreatePseudoConsole(new Coord { X = columns, Y = rows }, inputRead, outputWrite, 0, out pseudoConsole) != 0)
                return null;

            // The console owns its ends of the pipes now. Holding on to them would keep the output
            // stream from ever reaching end-of-file when the child exits.
            inputRead.Dispose();
            outputWrite.Dispose();
            inputRead = outputWrite = null;

            attributes = CreateAttributeListWith(pseudoConsole);
            if (attributes == nint.Zero)
                return null;

            // Standard handles are pinned to nothing on purpose. Where this app runs its own output
            // is a pipe — a container, a service, a test host — and a console child inherits those
            // pipes and writes to them instead of to the console it just attached to. Saying
            // "use these std handles" with none supplied leaves the child to open the pseudo
            // console's own, which is the entire point of attaching it.
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartFUseStdHandles,
                    hStdInput = nint.Zero,
                    hStdOutput = nint.Zero,
                    hStdError = nint.Zero,
                },
                lpAttributeList = attributes,
            };

            var executable = WindowsCommandLine.Resolve(fileName);
            var commandLine = WindowsCommandLine.NeedsCommandInterpreter(executable)
                ? WindowsCommandLine.BuildForCommandInterpreter(executable, arguments)
                : WindowsCommandLine.Build(executable, arguments);

            var block = WindowsCommandLine.BuildEnvironmentBlock(environment);
            var environmentBlock = Marshal.StringToHGlobalUni(block);

            try
            {
                var created = CreateProcess(
                    null,
                    new StringBuilder(commandLine),
                    nint.Zero,
                    nint.Zero,
                    // Handles reach the child through the pseudo console, not through inheritance.
                    false,
                    // Not CREATE_NO_WINDOW: that gives the child no console at all, and its output
                    // then goes nowhere instead of to the pseudo console attached below.
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environmentBlock,
                    string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                    ref startup,
                    out var process);

                if (!created)
                    return null;

                var channel = new ConPtyTerminalChannel(pseudoConsole, process.hProcess, process.hThread, inputWrite, outputRead);

                // Ownership has moved to the channel.
                pseudoConsole = nint.Zero;
                inputWrite = outputRead = null;

                return channel;
            }
            finally
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
        catch (Exception)
        {
            // Missing entry points on an older Windows land here.
            return null;
        }
        finally
        {
            if (attributes != nint.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (pseudoConsole != nint.Zero)
                ClosePseudoConsole(pseudoConsole);

            inputRead?.Dispose();
            outputWrite?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
        }
    }

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        await _writer.WriteAsync(text.AsMemory(), ct);
        await _writer.FlushAsync(ct);
    }

    /// <summary>
    /// Nothing to do: a pty has no separate standard input to close, and the caller sends Ctrl-D
    /// instead — which is what ends a read on a terminal without ending the session.
    /// </summary>
    public void CloseInput()
    {
    }

    public void Kill()
    {
        if (_processHandle != nint.Zero && IsRunning)
            TerminateProcess(_processHandle, 1);
    }

    private void WatchForExit()
    {
        var thread = new Thread(() =>
        {
            WaitForSingleObject(_processHandle, Infinite);
            ExitCode = GetExitCodeProcess(_processHandle, out var code) ? code : null;

            // Closing the console throws away whatever it still holds, and a command that prints and
            // exits immediately does both faster than the reader can drain — which reads as a command
            // that produced nothing at all. Let the pump catch up first.
            Thread.Sleep(DrainGrace);

            // Closing releases the last writer on the output pipe, which is what lets the reader see
            // end-of-file and stop.
            if (_pseudoConsole != nint.Zero)
                ClosePseudoConsole(_pseudoConsole);

            Exited?.Invoke();
        })
        {
            IsBackground = true,
            Name = "ConPTY exit watch",
        };

        thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        Kill();

        try
        {
            _writer.Dispose();
            _reader.Dispose();
        }
        catch (IOException)
        {
            // The pipe is already broken, which is the normal case here.
        }

        if (_threadHandle != nint.Zero)
            CloseHandle(_threadHandle);

        if (_processHandle != nint.Zero)
            CloseHandle(_processHandle);
    }

    /// <summary>
    /// Builds the attribute list that ties the new process to the pseudo console. The size is asked
    /// for once with a null buffer, which is the documented way to find out how big it has to be.
    /// </summary>
    private static nint CreateAttributeListWith(nint pseudoConsole)
    {
        var size = nuint.Zero;
        InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);

        var list = Marshal.AllocHGlobal((int)size);

        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            return nint.Zero;
        }

        if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributePseudoConsole, pseudoConsole, (nuint)nint.Size, nint.Zero, nint.Zero))
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            return nint.Zero;
        }

        return list;
    }

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint Infinite = 0xFFFFFFFF;
    private const int StartFUseStdHandles = 0x00000100;

    /// <summary>How long the reader gets to drain the console after the command exits.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(400);
    private static readonly nuint ProcThreadAttributePseudoConsole = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int attributeCount, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out int exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
