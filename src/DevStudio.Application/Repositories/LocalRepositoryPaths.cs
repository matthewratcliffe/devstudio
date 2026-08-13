namespace DevStudio.Application.Repositories;

/// <summary>
/// Path rules for repositories that live on a host bind mount instead of the data volume: which
/// paths the UI may reach, and where worktrees cut from them belong.
///
/// The allow-list is the security boundary. Without it, a folder picker in the UI is a browser for
/// the whole container filesystem, credentials volume included.
/// </summary>
public static class LocalRepositoryPaths
{
    /// <summary>Windows paths are case-insensitive; Linux ones are not, and the container is Linux.</summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Absolute, separator-normalised, de-duplicated roots. Blank entries are dropped.</summary>
    public static IReadOnlyList<string> NormaliseRoots(IEnumerable<string>? roots)
    {
        if (roots is null)
            return [];

        var normalised = new List<string>();

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var full = Normalise(root);
            if (!normalised.Any(existing => string.Equals(existing, full, Comparison)))
                normalised.Add(full);
        }

        return normalised;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> and confirms it is one of the roots or sits under one.
    /// Symlinks are followed first, so a link inside a root cannot point out of it.
    /// </summary>
    public static bool TryResolveWithinRoots(string? path, IEnumerable<string>? roots, out string resolved)
    {
        resolved = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalisedRoots = NormaliseRoots(roots);
        if (normalisedRoots.Count == 0)
            return false;

        var candidate = Resolve(path);

        foreach (var root in normalisedRoots)
        {
            // A filesystem root keeps its trailing separator, so appending another one would look for
            // "C:\\" and match nothing.
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

            if (string.Equals(candidate, root, Comparison) || candidate.StartsWith(prefix, Comparison))
            {
                resolved = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every drive that is currently mounted and readable: <c>C:\</c>, <c>D:\</c> and any attached
    /// removable or network drive on Windows, and the filesystem root elsewhere. Used as the root set
    /// when <c>AllowAllLocalDrives</c> is on, so the picker can reach a repository wherever it sits.
    /// Recomputed on each call rather than cached — drives come and go while the app is running.
    /// </summary>
    public static IReadOnlyList<string> DriveRoots()
    {
        var roots = new List<string>();

        foreach (var drive in SafeDrives())
        {
            // An unformatted card reader or a disconnected network drive is listed by the OS but
            // throws the moment anything looks inside it.
            try
            {
                if (!drive.IsReady)
                    continue;

                roots.Add(Normalise(drive.RootDirectory.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return roots;
    }

    private static DriveInfo[] SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>True for a work tree or a bare clone. `.git` is a file inside a worktree, not a directory.</summary>
    public static bool IsGitRepository(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) ||
        File.Exists(Path.Combine(path, ".git")) ||
        (Directory.Exists(Path.Combine(path, "refs")) && File.Exists(Path.Combine(path, "HEAD")));

    /// <summary>
    /// Directory worktrees for a repository are cut into. Volume clones keep using the shared
    /// worktrees path; a host-mounted repo gets a sibling folder on the mount, because a worktree
    /// on the data volume is invisible to the IDE that the mount exists to serve.
    /// </summary>
    public static string WorktreeRoot(bool isLocal, string localPath, string worktreesPath, string localFolderName)
    {
        if (!isLocal)
            return worktreesPath;

        var parent = Path.GetDirectoryName(Normalise(localPath));
        var folder = string.IsNullOrWhiteSpace(localFolderName) ? ".devstudio-worktrees" : localFolderName.Trim();

        // A repo mounted at the filesystem root has no parent to put a sibling folder in.
        return parent is null ? Path.Combine(localPath, folder) : Path.Combine(parent, folder);
    }

    private static string Normalise(string path)
    {
        var full = Path.GetFullPath(path.Trim());

        // Keep "/" and "C:\" whole; strip the trailing separator from everything else so prefix
        // comparisons do not depend on how the path was typed.
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || (OperatingSystem.IsWindows() && trimmed.EndsWith(':')) ? full : trimmed;
    }

    private static string Resolve(string path)
    {
        var full = Normalise(path);

        try
        {
            var info = new DirectoryInfo(full);
            if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return Normalise(target.FullName);
        }
        catch (IOException)
        {
            // An unreadable or cyclic link is not something to browse into; fall through and let
            // the prefix check judge the unresolved path.
        }

        return full;
    }
}
