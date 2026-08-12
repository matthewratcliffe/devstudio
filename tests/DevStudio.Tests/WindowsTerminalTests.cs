using DevStudio.Application.Common;
using DevStudio.Infrastructure.Terminals;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevStudio.Tests;

public class WindowsCommandLineTests
{
    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("--with-token", "--with-token")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData(@"C:\Program Files\git\git.exe", "\"C:\\Program Files\\git\\git.exe\"")]
    // Backslashes are only special in front of a quote, which is the rule everyone gets wrong.
    [InlineData(@"C:\path\", @"C:\path\")]
    [InlineData(@"a\b c", "\"a\\b c\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    public void Arguments_are_quoted_the_way_windows_takes_them_apart(string argument, string expected) =>
        Assert.Equal(expected, WindowsCommandLine.Quote(argument));

    [Fact]
    public void A_trailing_backslash_inside_quotes_is_doubled()
    {
        // Otherwise it escapes the closing quote and the rest of the command line is swallowed.
        Assert.Equal("\"C:\\path with space\\\\\"", WindowsCommandLine.Quote(@"C:\path with space\"));
    }

    [Fact]
    public void A_command_line_is_the_file_name_and_its_arguments()
    {
        var line = WindowsCommandLine.Build("gh", ["auth", "login", "--with-token"]);

        Assert.Equal("gh auth login --with-token", line);
    }

    [Theory]
    [InlineData("claude.cmd", true)]
    [InlineData("CLAUDE.CMD", true)]
    [InlineData("npm.bat", true)]
    [InlineData("git.exe", false)]
    public void Batch_wrappers_need_the_command_interpreter(string executable, bool expected) =>
        Assert.Equal(expected, WindowsCommandLine.NeedsCommandInterpreter(executable));

    [Fact]
    public void A_batch_wrapper_is_wrapped_in_cmd()
    {
        // npm installs claude as claude.cmd, and CreateProcess cannot run a script.
        var line = WindowsCommandLine.BuildForCommandInterpreter(@"C:\npm\claude.cmd", ["setup-token"]);

        Assert.Equal("cmd.exe /s /c \"\"C:\\npm\\claude.cmd\" setup-token\"", line);
    }

    [Fact]
    public void The_environment_block_is_null_separated_and_double_null_terminated()
    {
        var block = WindowsCommandLine.BuildEnvironmentBlock(new Dictionary<string, string>
        {
            ["DEVSTUDIO_TEST_ONE"] = "1",
        });

        Assert.Contains("DEVSTUDIO_TEST_ONE=1\0", block, StringComparison.Ordinal);
        Assert.EndsWith("\0\0", block, StringComparison.Ordinal);

        // The caller's values are merged over the real environment, not used instead of it.
        Assert.Contains("PATH=", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_override_replaces_the_inherited_value()
    {
        var block = WindowsCommandLine.BuildEnvironmentBlock(new Dictionary<string, string>
        {
            ["PATH"] = "only-this",
        });

        Assert.Contains("PATH=only-this\0", block, StringComparison.OrdinalIgnoreCase);
    }
}

public class WindowsTerminalTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "devstudio-pty-" + Guid.NewGuid().ToString("n"));

    public WindowsTerminalTests() => Directory.CreateDirectory(_home);

    /// <summary>
    /// The whole point of ConPTY: the child has to believe it is on a terminal. Every interactive
    /// login in this app depends on that being true, and the failure without it is silent — the CLI
    /// simply declines to start its device-code flow.
    /// </summary>
    [Fact]
    public async Task A_windows_terminal_session_is_a_real_terminal()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new TerminalService(
            Options.Create(new OrchestratorOptions { HomePath = _home }),
            NullLogger<TerminalService>.Instance);

        await using (service as IAsyncDisposable ?? throw new InvalidOperationException())
        {
            var session = await service.StartAsync(
                "powershell",
                ["-NoProfile", "-NonInteractive", "-Command", "[Console]::IsOutputRedirected"]);

            var answer = await WaitForOutputAsync(session, TimeSpan.FromSeconds(45));

            // True would mean a pipe — which is exactly what this replaced.
            Assert.Contains("False", answer, StringComparison.Ordinal);
            Assert.DoesNotContain("True", answer, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_terminal_reports_the_size_it_was_given()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var service = new TerminalService(
            Options.Create(new OrchestratorOptions { HomePath = _home }),
            NullLogger<TerminalService>.Instance);

        await using (service as IAsyncDisposable ?? throw new InvalidOperationException())
        {
            var session = await service.StartAsync(
                "powershell",
                ["-NoProfile", "-NonInteractive", "-Command", "$Host.UI.RawUI.WindowSize.Width"]);

            var answer = await WaitForOutputAsync(session, TimeSpan.FromSeconds(45));

            // A CLI that draws a full-screen UI asks the terminal how wide it is; on pipes there is
            // no answer at all.
            Assert.Contains("120", answer, StringComparison.Ordinal);
        }
    }

    private static async Task<string> WaitForOutputAsync(Application.Abstractions.ITerminalSession session, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (session.ExitCode is not null)
            {
                // Exit is announced from the watcher thread, and the last of the console output is
                // still being drained by the pump when it fires. Give it a moment to land.
                await Task.Delay(750);
                return session.Buffer;
            }

            await Task.Delay(150);
        }

        return session.Buffer;
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }
}
