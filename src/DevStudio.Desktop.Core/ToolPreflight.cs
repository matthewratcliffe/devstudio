using System.Text;

namespace DevStudio.Desktop;

/// <summary>
/// The container image bakes in git, node, the two AI CLIs and the two forge CLIs. A desktop install
/// bakes in nothing, so a missing tool shows up as an agent that mysteriously fails — this turns that
/// into a sentence naming the tool and how to get it.
/// </summary>
public static class ToolPreflight
{
    public sealed record Tool(string Executable, string Purpose, bool Required)
    {
        /// <summary>Install instructions worth reading are the ones for the machine in front of you.</summary>
        public string Install => Executable switch
        {
            "git" when OperatingSystem.IsWindows() => "https://git-scm.com/download/win",
            "git" when OperatingSystem.IsMacOS() => "xcode-select --install",
            "git" => "sudo apt install git",

            "node" when OperatingSystem.IsWindows() => "winget install OpenJS.NodeJS.LTS",
            "node" when OperatingSystem.IsMacOS() => "brew install node",
            "node" => "https://nodejs.org/en/download/package-manager",

            "claude" => "npm install -g @anthropic-ai/claude-code",
            "codex" => "npm install -g @openai/codex",

            "gh" when OperatingSystem.IsWindows() => "winget install GitHub.cli",
            "gh" when OperatingSystem.IsMacOS() => "brew install gh",
            "gh" => "sudo apt install gh",

            "glab" when OperatingSystem.IsWindows() => "winget install GLab.GLab",
            "glab" when OperatingSystem.IsMacOS() => "brew install glab",
            "glab" => "https://gitlab.com/gitlab-org/cli/-/releases",

            "rg" when OperatingSystem.IsWindows() => "winget install BurntSushi.ripgrep.MSVC",
            "rg" when OperatingSystem.IsMacOS() => "brew install ripgrep",
            _ => "sudo apt install ripgrep",
        };
    }

    private static readonly Tool[] Tools =
    [
        new("git", "Clones, worktrees and everything an agent commits", true),
        new("node", "Both AI CLIs ship as npm packages", true),
        new("claude", "Claude Code agents", false),
        new("codex", "OpenAI Codex agents", false),
        new("gh", "GitHub: repo lists, pull requests, issues", false),
        new("glab", "GitLab: repo lists, merge requests, issues", false),
        new("rg", "The search tool the claude CLI reaches for", false),
    ];

    public sealed record Result(Tool Tool, string? Path)
    {
        public bool Found => Path is not null;
    }

    public static IReadOnlyList<Result> Check() =>
        Tools.Select(tool => new Result(tool, Find(tool.Executable))).ToList();

    /// <summary>True when something an agent cannot work without is missing.</summary>
    public static bool HasBlockingGap(IReadOnlyList<Result> results) =>
        results.Any(r => r.Tool.Required && !r.Found);

    public static string Describe(IReadOnlyList<Result> results)
    {
        var report = new StringBuilder();

        foreach (var result in results.Where(r => r.Found))
            report.AppendLine($"OK       {result.Tool.Executable}  —  {result.Path}");

        var missing = results.Where(r => !r.Found).ToList();
        if (missing.Count > 0)
        {
            report.AppendLine();

            foreach (var result in missing)
            {
                report.AppendLine($"MISSING  {result.Tool.Executable}{(result.Tool.Required ? " (required)" : "")}");
                report.AppendLine($"         {result.Tool.Purpose}");
                report.AppendLine($"         {result.Tool.Install}");
                report.AppendLine();
            }

            report.AppendLine("Install what you need, then restart devStudio so it picks up the new PATH.");
        }

        return report.ToString();
    }

    /// <summary>
    /// Resolves against PATH the way a shell would. On Windows that means honouring PATHEXT, so
    /// <c>claude.cmd</c> — which is what npm actually installs there — is found as readily as an
    /// .exe; elsewhere it means checking the executable bit rather than guessing at extensions.
    /// </summary>
    public static string? Find(string executable)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Prepend(string.Empty)
            : [string.Empty];

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), executable + extension);

                    if (!File.Exists(candidate))
                        continue;

                    // A non-executable file of the right name on PATH is not the tool.
                    if (!OperatingSystem.IsWindows() &&
                        !File.GetUnixFileMode(candidate).HasFlag(UnixFileMode.UserExecute))
                        continue;

                    return candidate;
                }
                catch (Exception)
                {
                    // A malformed PATH entry is not worth failing the whole check over.
                }
            }
        }

        return null;
    }
}
