using DevStudio.Domain.Images;

namespace DevStudio.Application.Abstractions;

/// <summary>An image handed to a backend to work from, for the backends that can edit rather than only create.</summary>
public sealed record ImageInput(byte[] Content, string ContentType);

/// <summary>What the caller wants drawn. Backends clamp anything they cannot honour.</summary>
public sealed record ImageRequest
{
    public required string Prompt { get; init; }

    public int Width { get; init; } = 1024;
    public int Height { get; init; } = 1024;

    /// <summary>Backend-specific model name. Null takes the configured default.</summary>
    public string? Model { get; init; }

    /// <summary>Same seed and same prompt gives the same image, on the backends that support it.</summary>
    public int? Seed { get; init; }

    /// <summary>An image to edit rather than start from nothing. Ignored by backends that cannot.</summary>
    public ImageInput? Input { get; init; }
}

/// <summary>Raw result from a backend, before it is written anywhere.</summary>
public sealed record ImageBytes(byte[] Content, string ContentType, string Model);

/// <summary>Whether a backend could be called right now, and if not, what is missing.</summary>
public sealed record ImageAvailability(bool Configured, string Detail);

/// <summary>
/// One image service. Implementations differ only in how they are addressed and paid for, which is
/// the point: a quota wall means changing which one is selected, not changing any calling code.
/// </summary>
public interface IImageGenerator
{
    ImageBackend Backend { get; }

    /// <summary>Human-readable name, for the UI.</summary>
    string DisplayName { get; }

    /// <summary>Whether <see cref="ImageRequest.Input"/> means anything to this backend.</summary>
    bool SupportsImageInput { get; }

    /// <summary>Models offered in the UI. Free text is always allowed as well.</summary>
    IReadOnlyList<string> Models { get; }

    /// <summary>Checks configuration only — no request is sent, so this is free to call on render.</summary>
    ImageAvailability Check();

    Task<ImageBytes> GenerateAsync(ImageRequest request, CancellationToken ct = default);
}

/// <summary>
/// The image settings as they stand, cached so the backends can read them without an await on every
/// render. Everything here is edited on the Logins page rather than in configuration: these are
/// credentials, and they belong where the operator manages the other credentials.
/// </summary>
public interface IImageSettingsService
{
    /// <summary>Last loaded snapshot. Defaults until <see cref="LoadAsync"/> has run once.</summary>
    ImageSettings Current { get; }

    /// <summary>Re-reads from the store and refreshes <see cref="Current"/>.</summary>
    Task<ImageSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(ImageSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Generates through whichever backend was asked for, stores the result on the volume and records it.
/// The gallery and the agent tool both go through here, so an image made either way shows up in both.
/// </summary>
public interface IImageGenerationService
{
    /// <summary>Every backend, configured or not — the UI shows what is missing rather than hiding it.</summary>
    IReadOnlyList<IImageGenerator> Backends { get; }

    /// <summary>Backend used when the caller does not name one.</summary>
    ImageBackend DefaultBackend { get; }

    /// <summary>True when at least one backend could be called, which is what gates the agent tool.</summary>
    bool AnyConfigured { get; }

    Task<GeneratedImage> GenerateAsync(
        ImageRequest request,
        ImageBackend? backend = null,
        string? sessionId = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<GeneratedImage>> GetAllAsync(CancellationToken ct = default);

    /// <summary>The record behind a served file, so a download can be given a name worth reading.</summary>
    Task<GeneratedImage?> FindByFileNameAsync(string fileName, CancellationToken ct = default);

    /// <summary>File name to save as: the prompt, trimmed to something a filesystem will accept.</summary>
    string DownloadNameFor(GeneratedImage image);

    Task<bool> DeleteAsync(string imageId, CancellationToken ct = default);

    /// <summary>Absolute path of the folder holding generated images.</summary>
    string GetImagesPath();

    /// <summary>URL the browser fetches this image from.</summary>
    string UrlFor(GeneratedImage image);
}
