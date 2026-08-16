using DevStudio.Application.Abstractions;
using DevStudio.Domain.Sessions;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Workspaces;

/// <summary>
/// Lists and serves files from a session's working directory. Every path is resolved and checked to
/// be inside that directory before anything is opened — a session id and a relative path arrive
/// from the browser, so neither can be trusted.
/// </summary>
public sealed class WorkspaceFileService : IWorkspaceFileService
{
    /// <summary>Directories that are never worth showing.</summary>
    private static readonly string[] SkippedDirectories =
    [
        ".git", "node_modules", "bin", "obj", ".venv", "__pycache__", ".next", "dist", "target",
    ];

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
        [".pdf"] = "application/pdf",
        [".json"] = "application/json",
        [".csv"] = "text/csv",
        [".md"] = "text/markdown",
        [".txt"] = "text/plain",
        [".html"] = "text/html",
        [".zip"] = "application/zip",
    };

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp"];

    private static readonly string[] TextExtensions =
    [
        ".md", ".txt", ".json", ".yaml", ".yml", ".csv", ".xml", ".html", ".css", ".js", ".ts",
        ".cs", ".py", ".sh", ".sql", ".toml", ".ini", ".log", ".razor", ".tsx", ".jsx", ".svg",
    ];

    private readonly IEntityStore<ChatSession> _sessions;
    private readonly ILogger<WorkspaceFileService> _logger;
    private readonly WorkspacePathPolicy _policy;

    public WorkspaceFileService(
        IEntityStore<ChatSession> sessions,
        ILogger<WorkspaceFileService> logger,
        WorkspacePathPolicy? policy = null)
    {
        _sessions = sessions;
        _logger = logger;
        _policy = policy ?? new WorkspacePathPolicy();
    }

    public async Task<IReadOnlyList<WorkspaceFile>> ListAsync(string sessionId, int limit = 200, CancellationToken ct = default)
    {
        var root = await GetRootAsync(sessionId, ct);
        if (root is null)
            return [];

        try
        {
            return EnumerateFiles(root, _policy)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(file =>
                {
                    var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                    var extension = file.Extension.ToLowerInvariant();

                    return new WorkspaceFile(
                        relative,
                        file.Name,
                        file.Length,
                        new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                        ImageExtensions.Contains(extension),
                        TextExtensions.Contains(extension));
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the workspace for session {SessionId}", sessionId);
            return [];
        }
    }

    public async Task<(Stream Content, string FileName, string ContentType)?> OpenAsync(
        string sessionId,
        string relativePath,
        CancellationToken ct = default)
    {
        var root = await GetRootAsync(sessionId, ct);
        if (root is null || string.IsNullOrWhiteSpace(relativePath))
            return null;

        if (!WorkspacePathGuard.TryResolveWithin(
                root,
                relativePath,
                out var candidate,
                _policy.ValidatePaths,
                _policy.FollowSymlinks))
        {
            _logger.LogWarning("Refused a workspace path outside session {SessionId}", sessionId);
            return null;
        }

        if (!File.Exists(candidate))
            return null;

        var extension = Path.GetExtension(candidate).ToLowerInvariant();
        var contentType = ContentTypes.TryGetValue(extension, out var known) ? known : "application/octet-stream";

        return (File.OpenRead(candidate), Path.GetFileName(candidate), contentType);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private async Task<string?> GetRootAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(sessionId, ct);
        if (session is null || string.IsNullOrWhiteSpace(session.WorkingDirectory))
            return null;

        var root = Path.GetFullPath(session.WorkingDirectory);
        return Directory.Exists(root) ? root : null;
    }

    private static IEnumerable<string> EnumerateFiles(string root, WorkspacePathPolicy policy)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var directory = queue.Dequeue();

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (IsLink(child) && (!policy.FollowSymlinks ||
                    !WorkspacePathGuard.TryResolveWithin(
                        root,
                        child,
                        out _,
                        policy.ValidatePaths,
                        policy.FollowSymlinks)))
                    continue;

                var name = Path.GetFileName(child);
                if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                queue.Enqueue(child);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsLink(file) || (policy.FollowSymlinks &&
                    WorkspacePathGuard.TryResolveWithin(
                        root,
                        file,
                        out _,
                        policy.ValidatePaths,
                        policy.FollowSymlinks)))
                    yield return file;
            }
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
