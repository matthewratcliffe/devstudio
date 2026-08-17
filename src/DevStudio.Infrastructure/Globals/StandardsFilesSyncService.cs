using DevStudio.Application.Abstractions;
using DevStudio.Application.Globals;
using DevStudio.Application.Repositories;
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

            var (imported, removed) = await ImportAsync(settings, folder, log, ct);

            settings.FilesLastSyncedAt = DateTimeOffset.UtcNow;
            settings.FilesLastError = null;
            settings.FilesLastLog = log;
            await _globals.UpsertAsync(settings, ct);

            var message = imported == 0 && removed == 0
                ? "Nothing to import — the folder holds no files."
                : $"Imported {imported} file{(imported == 1 ? "" : "s")}" +
                  $"{(removed > 0 ? $", removed {removed}" : string.Empty)}.";

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

    private async Task<(int Imported, int Removed)> ImportAsync(
        GlobalSettings settings,
        string folder,
        List<string> log,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imported = 0;

        foreach (var file in Directory.EnumerateFiles(folder)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetFileName(file);
            var contentType = ContentTypeOf(relative);

            await using (var stream = File.OpenRead(file))
                await _files.SaveAsync(FileScope.Global, relative, stream, contentType, relative, ct);

            seen.Add(relative);
            imported++;
        }

        if (imported > 0)
            log.Add($"{imported} file{(imported == 1 ? "" : "s")}.");

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

        return (imported, removed);
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
