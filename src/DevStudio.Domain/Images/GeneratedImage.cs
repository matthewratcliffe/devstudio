using DevStudio.Domain.Common;

namespace DevStudio.Domain.Images;

/// <summary>
/// One image that was actually produced, and enough about how it was produced to make it again.
/// The bytes live on the volume; this is the record that points at them.
/// </summary>
public sealed class GeneratedImage : Entity
{
    public string Prompt { get; set; } = string.Empty;

    public ImageBackend Backend { get; set; } = ImageBackend.Pollinations;

    /// <summary>Model the backend actually used, as it names it.</summary>
    public string Model { get; set; } = string.Empty;

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Set when the caller asked for a specific seed, so the image can be reproduced.</summary>
    public int? Seed { get; set; }

    /// <summary>File name on the volume, inside the images folder. Never a path.</summary>
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "image/jpeg";
    public long SizeBytes { get; set; }

    /// <summary>Set when an agent generated this mid-turn, so the transcript and the gallery agree.</summary>
    public string? SessionId { get; set; }
}
