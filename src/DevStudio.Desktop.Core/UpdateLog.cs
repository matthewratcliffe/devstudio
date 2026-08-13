using Velopack.Logging;

namespace DevStudio.Desktop;

/// <summary>
/// Where the updater says what it did.
///
/// Updating is entirely a background activity — it never prompts, and every failure it can have is
/// swallowed so a missing network does not become a dialog. That is the right behaviour and it is
/// also completely opaque: without this file, an update that never arrives leaves nothing behind to
/// explain itself. Velopack writes here too, so the check, the download and the install are one
/// story in one file.
/// </summary>
public sealed class UpdateLog : IVelopackLogger
{
    // Big enough to hold weeks of six-hourly checks, small enough that nobody minds it existing.
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();

    public static UpdateLog Instance { get; } = new();

    private UpdateLog()
    {
    }

    public static string Path => System.IO.Path.Combine(DesktopPaths.DataRoot, "update.log");

    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        // Velopack's trace and debug lines are for debugging Velopack, not for reading later.
        if (logLevel < VelopackLogLevel.Information || string.IsNullOrWhiteSpace(message))
            return;

        Write(exception is null ? message : $"{message} — {exception.Message}");
    }

    /// <summary>Records one line. Never throws: a log that can fail a shutdown is worse than no log.</summary>
    public void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DesktopPaths.DataRoot);

                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                    File.Delete(Path);

                File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Read-only disk, a full one, or a virus scanner holding the handle. Not worth caring about.
        }
    }
}
