using AiShop.Domain.Common;

namespace AiShop.Application.Abstractions;

/// <summary>
/// Which library a file belongs to: the global one that reaches every agent, or a single project's.
/// </summary>
public readonly record struct FileScope(string? ProjectId)
{
    /// <summary>Files that apply everywhere — setup notes, coding standards.</summary>
    public static FileScope Global => new((string?)null);

    public static FileScope Project(string projectId) => new(projectId);

    public bool IsGlobal => ProjectId is null;

    /// <summary>Stable identifier used in download URLs.</summary>
    public string Key => ProjectId ?? "global";

    public static FileScope FromKey(string key) =>
        string.IsNullOrWhiteSpace(key) || key == "global" ? Global : Project(key);
}

/// <summary>Stores uploaded reference files on the volume and keeps their owner's list in step.</summary>
public interface IFileLibraryService
{
    Task<StoredFile> SaveAsync(
        FileScope scope,
        string fileName,
        Stream content,
        string contentType,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(FileScope scope, string fileId, CancellationToken ct = default);

    /// <summary>Text preview for the UI. Returns null for binary files.</summary>
    Task<string?> ReadTextAsync(FileScope scope, string fileId, int maxChars = 20_000, CancellationToken ct = default);

    /// <summary>Absolute path of the scope's uploads folder.</summary>
    string GetFilesPath(FileScope scope);
}
