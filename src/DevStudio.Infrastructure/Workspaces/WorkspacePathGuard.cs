namespace DevStudio.Infrastructure.Workspaces;

/// <summary>Resolves paths lexically and through any existing symlinks before accepting them.</summary>
public static class WorkspacePathGuard
{
    public static bool TryResolveWithin(
        string root,
        string path,
        out string resolved,
        bool validatePaths = true,
        bool followSymlinks = false)
    {
        resolved = string.Empty;

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(rootFull, path));

        if (!validatePaths)
        {
            resolved = candidate;
            return true;
        }

        if (!IsWithin(rootFull, candidate))
            return false;

        var realRoot = followSymlinks ? ResolveLink(rootFull, out _) : rootFull;

        var relative = Path.GetRelativePath(rootFull, candidate);
        var current = realRoot;

        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var linkResolved = ResolveLink(current, out var isLink);
            if (isLink && !followSymlinks)
                return false;

            current = linkResolved;

            if (!IsWithin(realRoot, current))
                return false;
        }

        resolved = current;
        return true;
    }

    private static string ResolveLink(string path, out bool isLink)
    {
        isLink = false;
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            if (info.ResolveLinkTarget(returnFinalTarget: true)?.FullName is not { } target)
                return path;

            isLink = true;
            return Path.GetFullPath(target);
        }
        catch (IOException)
        {
            return path;
        }
        catch (UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidateFull = Path.GetFullPath(candidate);

        return string.Equals(rootFull, candidateFull, comparison)
            || candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison)
            || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                && candidateFull.StartsWith(rootFull + Path.AltDirectorySeparatorChar, comparison));
    }
}
