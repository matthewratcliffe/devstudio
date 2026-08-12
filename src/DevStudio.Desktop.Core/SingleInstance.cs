namespace DevStudio.Desktop;

/// <summary>
/// One server per machine.
///
/// Two files rather than one: a lock file held open exclusively, which is what a second launch trips
/// over, and a plain text file beside it holding the URL of the copy that holds the lock — so the
/// second launch has something useful to say instead of starting a rival server on another port.
/// Sharing one file does not work: an exclusive lock on Windows keeps the second process from
/// reading it at all.
///
/// Files rather than a named mutex, because this has to behave the same on macOS and Linux.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string LockFile = "instance.lock";
    private const string UrlFile = "instance.url";

    private readonly FileStream? _handle;

    private SingleInstance(FileStream? handle, string? runningUrl)
    {
        _handle = handle;
        RunningUrl = runningUrl;
    }

    public bool Acquired => _handle is not null;

    /// <summary>Where the instance already running is listening, when one is and it said so.</summary>
    public string? RunningUrl { get; }

    public static SingleInstance Acquire()
    {
        Directory.CreateDirectory(DesktopPaths.DataRoot);

        try
        {
            // DeleteOnClose keeps a crash from leaving a lock file that refuses every later launch;
            // FileShare.None is the part that actually excludes the second process, on every platform.
            var handle = new FileStream(
                Path.Combine(DesktopPaths.DataRoot, LockFile),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64,
                FileOptions.DeleteOnClose);

            return new SingleInstance(handle, null);
        }
        catch (IOException)
        {
            return new SingleInstance(null, ReadUrl());
        }
        catch (UnauthorizedAccessException)
        {
            return new SingleInstance(null, ReadUrl());
        }
    }

    /// <summary>Records where this instance is listening, for whoever tries to launch a second one.</summary>
    public void Publish(string url)
    {
        if (_handle is null)
            return;

        try
        {
            File.WriteAllText(Path.Combine(DesktopPaths.DataRoot, UrlFile), url);
        }
        catch (IOException)
        {
            // Only costs the second launch its hint.
        }
    }

    private static string? ReadUrl()
    {
        try
        {
            var path = Path.Combine(DesktopPaths.DataRoot, UrlFile);

            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } url ? url : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_handle is null)
            return;

        _handle.Dispose();

        try
        {
            // The URL is only true while this instance is up.
            File.Delete(Path.Combine(DesktopPaths.DataRoot, UrlFile));
        }
        catch (IOException)
        {
            // A stale URL file is harmless: the lock is what decides, not this.
        }
    }
}
