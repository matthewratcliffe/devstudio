namespace DevStudio.Infrastructure.Terminals;

/// <summary>
/// Turning a file name and a list of arguments into the single command-line string Windows actually
/// takes. <see cref="System.Diagnostics.Process"/> does this for you; CreateProcess, which is what a
/// pseudo console needs, does not.
/// </summary>
public static class WindowsCommandLine
{
    /// <summary>
    /// Quotes one argument by the rules CommandLineToArgvW uses to take it apart again — which are
    /// not the obvious ones: backslashes are only special immediately before a quote.
    /// </summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"'))
            return argument;

        var quoted = new System.Text.StringBuilder("\"");

        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;

            while (i < argument.Length && argument[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (i == argument.Length)
            {
                // Trailing backslashes would escape the closing quote, so they are doubled.
                quoted.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(argument[i]);
            }
        }

        return quoted.Append('"').ToString();
    }

    public static string Build(string fileName, IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Select(Quote).Prepend(Quote(fileName)));

    /// <summary>
    /// True for the batch wrappers npm writes on Windows. CreateProcess cannot run one — it is a
    /// script, not an image — so <c>claude</c>, which npm installs as <c>claude.cmd</c>, has to go
    /// through the command interpreter.
    /// </summary>
    public static bool NeedsCommandInterpreter(string executable) =>
        executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The command line for a batch wrapper. cmd's own quoting is not the C runtime's: with /s it
    /// strips one outer pair of quotes and passes the rest through untouched, which is the only
    /// reliable way to send a quoted path plus quoted arguments.
    /// </summary>
    public static string BuildForCommandInterpreter(string executable, IReadOnlyList<string> arguments)
    {
        // The path is quoted whether or not it needs to be: cmd splits on spaces before the C
        // runtime ever sees the line, and "C:\Program Files\..." is the common case.
        var command = string.Join(' ', arguments.Select(Quote).Prepend($"\"{executable}\""));

        return $"cmd.exe /s /c \"{command}\"";
    }

    /// <summary>
    /// Resolves a bare name against PATH the way a shell would, honouring PATHEXT. CreateProcess
    /// searches PATH but knows nothing about PATHEXT, so "claude" alone finds nothing at all.
    /// </summary>
    public static string Resolve(string fileName)
    {
        if (Path.IsPathRooted(fileName) || fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains('/'))
            return fileName;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // An explicit extension is used as given; only a bare name gets the PATHEXT treatment.
        // PATHEXT candidates come before the bare name itself: cmd.exe resolves an extensionless
        // command by trying each PATHEXT extension in turn, never the bare file, so a directory that
        // holds both an npm POSIX shim (plain "opencode", not runnable by CreateProcess) and its
        // Windows counterpart ("opencode.cmd") has to prefer the one Windows can actually launch.
        var candidates = Path.HasExtension(fileName)
            ? [fileName]
            : extensions.Select(extension => fileName + extension).Append(fileName);

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Windows npm installs global command shims in %APPDATA%\npm. Desktop applications can
        // retain the PATH from before Node/npm was installed, so include npm's standard per-user
        // bin directory even when it is missing from the inherited environment.
        if (OperatingSystem.IsWindows())
        {
            var npmBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");

            if (!string.IsNullOrWhiteSpace(npmBin) &&
                !directories.Any(path => string.Equals(path.TrimEnd('\\', '/'), npmBin.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            {
                directories.Add(npmBin);
            }
        }

        foreach (var directory in directories)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var full = Path.Combine(directory.Trim('"'), candidate);
                    if (File.Exists(full))
                        return full;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not worth failing the launch over.
                }
            }
        }

        return fileName;
    }

    /// <summary>
    /// The environment block CreateProcess expects: NAME=VALUE pairs separated by nulls, sorted, and
    /// terminated by an extra null.
    /// </summary>
    public static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                merged[key] = value;
        }

        foreach (var pair in environment)
            merged[pair.Key] = pair.Value;

        var block = new System.Text.StringBuilder();

        foreach (var pair in merged)
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');

        return block.Append('\0').ToString();
    }
}
