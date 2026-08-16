using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Common;
using DevStudio.Domain.Globals;
using DevStudio.Domain.Projects;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Workspaces;

/// <summary>
/// One implementation for both libraries — a global file and a project file differ only in which
/// folder they land in and which record lists them.
/// </summary>
public sealed class FileLibraryService : IFileLibraryService
{
    private static readonly string[] TextExtensions =
    [
        ".md", ".txt", ".json", ".yaml", ".yml", ".csv", ".xml", ".html", ".css", ".js", ".ts",
        ".cs", ".py", ".sh", ".sql", ".toml", ".ini", ".log", ".razor", ".tsx", ".jsx", ".editorconfig",
    ];

    private readonly IEntityStore<Project> _projects;
    private readonly IEntityStore<GlobalSettings> _globals;
    private readonly OrchestratorOptions _options;

    public FileLibraryService(
        IEntityStore<Project> projects,
        IEntityStore<GlobalSettings> globals,
        IOptions<OrchestratorOptions> options)
    {
        _projects = projects;
        _globals = globals;
        _options = options.Value;
    }

    public string GetFilesPath(FileScope scope) => scope.IsGlobal
        ? Path.Combine(_options.DataPath, "global", "files")
        : Path.Combine(_options.DataPath, "projects", scope.ProjectId!, "files");

    public async Task<StoredFile> SaveAsync(
        FileScope scope,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken ct = default)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new ArgumentException("A file name is required.", nameof(fileName));

        var files = await GetListAsync(scope, ct);
        var originalFiles = files.ToList();

        var directory = GetFilesPath(scope);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, safeName);
        var tempPath = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        var backupPath = path + "." + Guid.NewGuid().ToString("n") + ".bak";

        await using (var target = File.Create(tempPath))
        {
            await content.CopyToAsync(target, ct);
        }

        var hadOriginal = File.Exists(path);

        try
        {
            if (hadOriginal)
                File.Move(path, backupPath);
            File.Move(tempPath, path);

            var info = new FileInfo(path);
            var file = new StoredFile
            {
                FileName = safeName,
                ContentType = contentType,
                SizeBytes = info.Length,
                IsText = TextExtensions.Contains(Path.GetExtension(safeName).ToLowerInvariant()),
            };

            // Re-uploading the same name replaces the entry rather than duplicating it.
            files.RemoveAll(f => string.Equals(f.FileName, safeName, StringComparison.OrdinalIgnoreCase));
            files.Add(file);

            try
            {
                await SaveListAsync(scope, ct);
            }
            catch
            {
                files.Clear();
                files.AddRange(originalFiles);
                try
                {
                    await SaveListAsync(scope, CancellationToken.None);
                }
                catch
                {
                    // Preserve the original failure; the physical file is still restored below.
                }

                RestoreOriginalFile(path, backupPath, hadOriginal);
                throw;
            }

            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
            }
            catch
            {
                // The new file and metadata are committed; a failed cleanup is harmless and can
                // be removed on a later upload.
            }

            return file;
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            if (File.Exists(backupPath))
                RestoreOriginalFile(path, backupPath, hadOriginal);

            throw;
        }
    }

    private static void RestoreOriginalFile(string path, string backupPath, bool hadOriginal)
    {
        if (File.Exists(path))
            File.Delete(path);

        if (hadOriginal && File.Exists(backupPath))
            File.Move(backupPath, path);
    }

    public async Task<bool> DeleteAsync(FileScope scope, string fileId, CancellationToken ct = default)
    {
        var files = await GetListAsync(scope, ct);
        var file = files.FirstOrDefault(f => f.Id == fileId);
        if (file is null)
            return false;

        var path = Path.Combine(GetFilesPath(scope), file.FileName);
        if (File.Exists(path))
            File.Delete(path);

        files.Remove(file);
        await SaveListAsync(scope, ct);
        return true;
    }

    public async Task<string?> ReadTextAsync(
        FileScope scope,
        string fileId,
        int maxChars = 20_000,
        CancellationToken ct = default)
    {
        var files = await GetListAsync(scope, ct);
        var file = files.FirstOrDefault(f => f.Id == fileId);
        if (file is null || !file.IsText)
            return null;

        var path = Path.Combine(GetFilesPath(scope), file.FileName);
        if (!File.Exists(path))
            return null;

        var text = await File.ReadAllTextAsync(path, ct);
        return text.Length <= maxChars ? text : text[..maxChars] + "\n\n... (truncated)";
    }

    private async Task<List<StoredFile>> GetListAsync(FileScope scope, CancellationToken ct)
    {
        if (scope.IsGlobal)
            return (await GetGlobalAsync(ct)).Files;

        var project = await _projects.GetAsync(scope.ProjectId!, ct)
                      ?? throw new InvalidOperationException("That project no longer exists.");

        return project.Files;
    }

    private async Task SaveListAsync(FileScope scope, CancellationToken ct)
    {
        if (scope.IsGlobal)
        {
            await _globals.UpsertAsync(await GetGlobalAsync(ct), ct);
            return;
        }

        var project = await _projects.GetAsync(scope.ProjectId!, ct);
        if (project is not null)
            await _projects.UpsertAsync(project, ct);
    }

    private async Task<GlobalSettings> GetGlobalAsync(CancellationToken ct) =>
        await _globals.GetAsync(GlobalSettings.WellKnownId, ct) ?? new GlobalSettings();
}
