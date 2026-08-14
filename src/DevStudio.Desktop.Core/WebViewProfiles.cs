namespace DevStudio.Desktop;

/// <summary>
/// Where the embedded browser keeps its cache, and how a new build stops being served the old one's
/// pages.
///
/// Three reasonable decisions combine into an unreasonable outcome. The profile lives in the data
/// directory so an update does not throw away a signed-in session; the server always answers on the
/// same loopback origin so a bookmark keeps working; and an HTTP cache is keyed on the origin and
/// nothing else. Together they let a page rendered by a build from weeks ago outlive the build it
/// came from, and there is no address bar in the window to notice it with.
///
/// So the profile is keyed on the version as well. A new build opens a directory nothing has ever
/// written to, and the directories behind it are removed rather than left to accumulate — each one
/// is a browser profile, and they run to tens of megabytes apiece.
/// </summary>
public static class WebViewProfiles
{
    private const string StampFile = ".build";

    /// <summary>The profile directory for one version, under the root that holds all of them.</summary>
    public static string DirectoryFor(string root, string version) =>
        Path.Combine(root, Sanitise(version));

    /// <summary>
    /// Everything under the root that is not the profile in use: the versions this build replaced,
    /// and — for the first launch after this change — the single unversioned profile that used to
    /// sit directly in the root.
    /// </summary>
    public static IReadOnlyList<string> StaleIn(string root, string version)
    {
        if (!Directory.Exists(root))
            return [];

        var keep = Sanitise(version);

        return Directory.EnumerateFileSystemEntries(root)
            .Where(entry => !string.Equals(Path.GetFileName(entry), keep, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Deletes them, and reports what went. A profile belonging to a copy that is still running is
    /// locked and stays locked; it is dead weight rather than a problem, and it goes on the launch
    /// after that one exits.
    /// </summary>
    public static IReadOnlyList<string> Prune(string root, string version)
    {
        var removed = new List<string>();

        foreach (var entry in StaleIn(root, version))
        {
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);

                removed.Add(Path.GetFileName(entry));
            }
            catch (Exception ex)
            {
                UpdateLog.Instance.Write($"Left the web view profile {Path.GetFileName(entry)} in place: {ex.Message}");
            }
        }

        return removed;
    }

    /// <summary>
    /// True when this profile has not been used by this build before, which is the moment to throw
    /// the cache away. Keyed on the profile rather than the version so it still answers for a build
    /// that has no version to be keyed on — a development build is replaced by pressing build, and
    /// gets the same directory every time.
    /// </summary>
    public static bool CacheIsStale(string profile, string stamp)
    {
        try
        {
            var path = Path.Combine(profile, StampFile);
            return !File.Exists(path) || File.ReadAllText(path).Trim() != stamp;
        }
        catch
        {
            // Unreadable is indistinguishable from absent, and both mean clear it.
            return true;
        }
    }

    /// <summary>Records that this build has now cleared the cache, so the next launch does not.</summary>
    public static void RecordCache(string profile, string stamp)
    {
        try
        {
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, StampFile), stamp);
        }
        catch (Exception ex)
        {
            // The cost of failing here is clearing an already-clear cache on the next launch.
            UpdateLog.Instance.Write($"Could not record the web view build stamp: {ex.Message}");
        }
    }

    /// <summary>A version is not a path, and a tag nobody sanitised is a directory somewhere else.</summary>
    private static string Sanitise(string version)
    {
        var cleaned = new string(version
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)
            .ToArray())
            .Trim('.', ' ');

        return cleaned.Length == 0 ? "unknown" : cleaned;
    }
}
