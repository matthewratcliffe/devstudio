namespace AiShop.Application.Abstractions;

public sealed record WorkspaceFile(
    string RelativePath,
    string Name,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    bool IsImage,
    bool IsText);

/// <summary>
/// Reads what an agent produced in its own workspace, so output can be looked at and downloaded
/// without shelling into the container.
/// </summary>
public interface IWorkspaceFileService
{
    /// <summary>
    /// Files in the session's working directory, newest first. Noise like <c>.git</c> and
    /// <c>node_modules</c> is skipped.
    /// </summary>
    Task<IReadOnlyList<WorkspaceFile>> ListAsync(string sessionId, int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// Opens a file for download. Returns null when the session, or the path inside it, does not
    /// resolve — a path is never allowed to escape the workspace.
    /// </summary>
    Task<(Stream Content, string FileName, string ContentType)?> OpenAsync(
        string sessionId,
        string relativePath,
        CancellationToken ct = default);
}
