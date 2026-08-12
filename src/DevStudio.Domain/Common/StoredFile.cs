namespace DevStudio.Domain.Common;

/// <summary>
/// An uploaded reference file, held on the volume and staged into agent workspaces. Used by both the
/// global library and individual projects — the shape and the storage mechanics are identical.
/// </summary>
public sealed class StoredFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>Original file name, also the name it gets inside the workspace.</summary>
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set for text files so the UI can preview them without touching disk.</summary>
    public bool IsText { get; set; }
}
