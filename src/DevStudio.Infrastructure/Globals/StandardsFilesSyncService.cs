using DevStudio.Application.Abstractions;
using DevStudio.Application.Globals;
using DevStudio.Application.Repositories;
using DevStudio.Domain.Common;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Repositories;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Globals;

/// <summary>
/// Reads a folder of a git repository into the global file library. Every file it writes is marked
/// with the path it came from — <see cref="DevStudio.Domain.Common.StoredFile.TeamSourcePath"/> — which
/// is what makes a second sync an update instead of a duplicate, and what lets a file being deleted in
/// the repository remove it here too. A file uploaded by hand carries no such mark and is left alone.
/// </summary>
public sealed class StandardsFilesSyncService : IStandardsFilesSyncService
{
    private readonly IEntityStore<GlobalSettings> _globals;
    private readonly IEntityStore<GitRepository> _repositories;
    private readonly IFileLibraryService _files;
    private readonly IGitService _git;
    private readonly ILogger<StandardsFilesSyncService> _logger;

    public StandardsFilesSyncService(
        IEntityStore<GlobalSettings> globals,
        IEntityStore<GitRepository> repositories,
        IFileLibraryService files,
        IGitService git,
        ILogger<StandardsFilesSyncService> logger)
    {
        _globals = globals;
        _repositories = repositories;
        _files = files;
        _git = git;
        _logger = logger;
    }

    public async Task<StandardsSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var settings = await _globals.GetAsync(GlobalSettings.WellKnownId, ct) ?? new GlobalSettings();
        var log = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.FilesRepositoryId))
            return StandardsSyncResult.Failed("No standards files repository is configured.");

        try
        {
            var folder = await ResolveFolderAsync(settings, log, ct);

            if (settings.FilesPullBeforeSync)
                await PullAsync(settings, log, ct);

            var (imported, removed, unchanged) = await ImportAsync(settings, folder, log, ct);

            settings.FilesLastSyncedAt = DateTimeOffset.UtcNow;
            settings.FilesLastError = null;
            settings.FilesLastLog = log;
            await _globals.UpsertAsync(settings, ct);

            // "Nothing to import" used to cover both an empty folder and a folder that had simply not
            // changed, which are worth telling apart — the second is the normal state and should not
            // read as though the standards had gone missing.
            var message = (imported, removed, unchanged) switch
            {
                (0, 0, 0) => "Nothing to import — the folder holds no files.",
                (0, 0, _) => $"Up to date — {unchanged} file{(unchanged == 1 ? "" : "s")}, nothing changed.",
                _ => $"Imported {imported} file{(imported == 1 ? "" : "s")}" +
                     $"{(removed > 0 ? $", removed {removed}" : string.Empty)}" +
                     $"{(unchanged > 0 ? $", {unchanged} unchanged" : string.Empty)}.",
            };

            log.Add(message);
            return new StandardsSyncResult(true, message, log, imported, removed);
        }
        catch (Exception ex)
        {
            // Startup and pre-conversation callers have nobody watching, so a failure is reported
            // rather than thrown — it must never stop the app or a new session from starting.
            _logger.LogWarning(ex, "Standards files sync failed");
            log.Add(ex.Message);

            settings.FilesLastSyncedAt = DateTimeOffset.UtcNow;
            settings.FilesLastError = ex.Message;
            settings.FilesLastLog = log;
            await _globals.UpsertAsync(settings, CancellationToken.None);

            return StandardsSyncResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// The files folder inside the checkout. The folder is configuration, so it is resolved against
    /// the repository and refused if it points outside it.
    /// </summary>
    private async Task<string> ResolveFolderAsync(GlobalSettings settings, List<string> log, CancellationToken ct)
    {
        var repository = await _repositories.GetAsync(settings.FilesRepositoryId!, ct)
                         ?? throw new InvalidOperationException("The configured standards files repository no longer exists.");

        var candidate = string.IsNullOrWhiteSpace(settings.FilesFolder)
            ? repository.LocalPath
            : Path.Combine(repository.LocalPath, settings.FilesFolder.Replace('/', Path.DirectorySeparatorChar));

        if (!LocalRepositoryPaths.TryResolveWithinRoots(candidate, [repository.LocalPath], out var folder))
            throw new InvalidOperationException($"'{settings.FilesFolder}' points outside {repository.Name}.");

        if (!Directory.Exists(folder))
            throw new InvalidOperationException($"'{folder}' is not in the checkout of {repository.Name}.");

        log.Add($"Reading {repository.Name} at {folder}.");
        return folder;
    }

    private async Task PullAsync(GlobalSettings settings, List<string> log, CancellationToken ct)
    {
        var repository = await _repositories.GetAsync(settings.FilesRepositoryId!, ct);
        if (repository is null || string.IsNullOrWhiteSpace(repository.RemoteUrl))
        {
            log.Add("No origin remote, so nothing was pulled.");
            return;
        }

        try
        {
            var pull = await _git.RunAsync(repository.LocalPath, ["pull", "--ff-only"], ct);

            // A checkout with local work, or no network, is still worth importing as it stands — that
            // is less surprising than refusing to sync because a pull did not go through.
            log.Add(pull.Succeeded
                ? "Pulled the latest commit."
                : $"Could not pull, importing the checkout as it stands: {Trim(pull.Output)}");
        }
        catch (Exception ex)
        {
            log.Add($"Could not pull, importing the checkout as it stands: {Trim(ex.Message)}");
        }
    }

    private async Task<(int Imported, int Removed, int Unchanged)> ImportAsync(
        GlobalSettings settings,
        string folder,
        List<string> log,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var unchanged = 0;

        var library = _files.GetFilesPath(FileScope.Global);
        var manifest = (await _globals.GetAsync(GlobalSettings.WellKnownId, ct) ?? settings).Files;

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .Where(f => !IsUnderGitFolder(folder, f))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
            var flattenedName = relative.Replace("/", "__");
            var contentType = ContentTypeOf(relative);

            // This runs on a timer and again before every session starts, so rewriting all of them
            // each time was rewriting files nobody had changed. Every rewrite briefly swaps the
            // published file, and a session provisioning its workspace at that moment copies a
            // directory mid-change — so the churn was not merely wasteful, it was the thing making
            // the swap something a session could land in.
            if (IsAlreadyPublished(manifest, flattenedName, relative, Path.Combine(library, flattenedName), file))
            {
                seen.Add(relative);
                unchanged++;
                continue;
            }

            await using (var stream = File.OpenRead(file))
                await _files.SaveAsync(FileScope.Global, flattenedName, stream, contentType, relative, ct);

            seen.Add(relative);
            imported++;
        }

        if (imported > 0)
            log.Add($"{imported} file{(imported == 1 ? "" : "s")}.");

        if (unchanged > 0)
            log.Add($"{unchanged} already up to date.");

        var removed = 0;
        var current = await _globals.GetAsync(GlobalSettings.WellKnownId, ct) ?? settings;

        foreach (var orphan in current.Files
                     .Where(f => f.TeamSourcePath is { Length: > 0 } && !seen.Contains(f.TeamSourcePath))
                     .ToList())
        {
            await _files.DeleteAsync(FileScope.Global, orphan.Id, ct);
            log.Add($"Removed '{orphan.FileName}' — its file has gone.");
            removed++;
        }

        return (imported, removed, unchanged);
    }

    /// <summary>
    /// Whether the library already holds exactly this file, from exactly this source path. Both
    /// halves matter: identical bytes on disk are not enough if nothing lists the file, because the
    /// listing is what the UI shows and what an orphan sweep works from.
    /// </summary>
    private static bool IsAlreadyPublished(
        IReadOnlyList<StoredFile> manifest,
        string flattenedName,
        string relative,
        string publishedPath,
        string sourcePath)
    {
        var listed = manifest.Any(f =>
            string.Equals(f.FileName, flattenedName, StringComparison.OrdinalIgnoreCase) &&
            f.TeamSourcePath == relative);

        if (!listed || !File.Exists(publishedPath))
            return false;

        try
        {
            var published = new FileInfo(publishedPath);
            var source = new FileInfo(sourcePath);

            // Length first, which settles almost every case without reading anything. Content rather
            // than timestamps because a fresh clone or a checkout rewrites mtimes without the bytes
            // having changed at all.
            return published.Length == source.Length &&
                   File.ReadAllBytes(publishedPath).AsSpan().SequenceEqual(File.ReadAllBytes(sourcePath));
        }
        catch
        {
            // Unreadable for any reason: fall through and republish, which is the safe direction.
            return false;
        }
    }

    private static bool IsUnderGitFolder(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private static string ContentTypeOf(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".md" or ".txt" => "text/markdown",
            ".json" => "application/json",
            ".yaml" or ".yml" => "application/x-yaml",
            _ => "application/octet-stream",
        };

    private static string Trim(string output) =>
        output.Length <= 300 ? output.Trim() : output.Trim()[..300] + "…";
}
