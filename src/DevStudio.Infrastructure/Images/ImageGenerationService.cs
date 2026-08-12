using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Images;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Images;

/// <summary>
/// The one way an image gets made. Picks a backend, writes the bytes to the volume and records what
/// it was — so an image an agent produced mid-turn and one made from the gallery are the same thing
/// afterwards, and neither depends on the backend that happened to draw it.
/// </summary>
public sealed class ImageGenerationService : IImageGenerationService
{
    /// <summary>Below this, a backend is producing thumbnails; above it, nothing free will answer.</summary>
    private const int MinDimension = 64;

    private readonly Dictionary<ImageBackend, IImageGenerator> _byBackend;
    private readonly IEntityStore<GeneratedImage> _store;
    private readonly IImageSettingsService _settings;
    private readonly OrchestratorOptions _orchestrator;
    private readonly ILogger<ImageGenerationService> _logger;

    public ImageGenerationService(
        IEnumerable<IImageGenerator> generators,
        IEntityStore<GeneratedImage> store,
        IImageSettingsService settings,
        IOptions<OrchestratorOptions> orchestrator,
        ILogger<ImageGenerationService> logger)
    {
        _byBackend = generators.ToDictionary(g => g.Backend);
        _store = store;
        _settings = settings;
        _orchestrator = orchestrator.Value;
        _logger = logger;

        Backends = _byBackend.Values.OrderBy(g => g.Backend).ToList();
    }

    public IReadOnlyList<IImageGenerator> Backends { get; }

    public ImageBackend DefaultBackend => _settings.Current.DefaultBackend;

    public bool AnyConfigured => Backends.Any(g => g.Check().Configured);

    public string GetImagesPath() => Path.Combine(_orchestrator.DataPath, "images");

    public string UrlFor(GeneratedImage image) => $"/images/{Uri.EscapeDataString(image.FileName)}";

    public async Task<GeneratedImage> GenerateAsync(
        ImageRequest request,
        ImageBackend? backend = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("A prompt is required.", nameof(request));

        // Re-read before every generation, so a key added on the Logins page works on the next
        // image rather than after a restart — and so a turn running now picks it up too.
        var settings = await _settings.LoadAsync(ct);

        var selected = backend ?? settings.DefaultBackend;

        if (!_byBackend.TryGetValue(selected, out var generator))
            throw new InvalidOperationException($"No image backend is registered for {selected}.");

        // Refused rather than dropped: silently ignoring the input would return a fresh picture with
        // nothing to say the edit never happened.
        if (request.Input is not null && !generator.SupportsImageInput)
            throw new InvalidOperationException($"{generator.DisplayName} cannot edit an existing image. Use Gemini for that.");

        var clamped = request with
        {
            Width = Math.Clamp(request.Width, MinDimension, settings.MaxDimension),
            Height = Math.Clamp(request.Height, MinDimension, settings.MaxDimension),
        };

        var result = await generator.GenerateAsync(clamped, ct);

        var image = new GeneratedImage
        {
            Prompt = request.Prompt,
            Backend = selected,
            Model = result.Model,
            Width = clamped.Width,
            Height = clamped.Height,
            Seed = request.Seed,
            ContentType = result.ContentType,
            SizeBytes = result.Content.LongLength,
            SessionId = sessionId,
        };

        // Named after the record so the file and the entity cannot drift apart, and so nothing a
        // model wrote ever reaches the filesystem as a name.
        image.FileName = $"{image.Id}{ExtensionFor(result.ContentType)}";

        var directory = GetImagesPath();
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, image.FileName), result.Content, ct);

        await _store.UpsertAsync(image, ct);

        _logger.LogInformation(
            "Generated {FileName} with {Backend}/{Model} ({Bytes} bytes)",
            image.FileName, selected, result.Model, result.Content.LongLength);

        return image;
    }

    public async Task<IReadOnlyList<GeneratedImage>> GetAllAsync(CancellationToken ct = default)
    {
        var images = await _store.GetAllAsync(ct);
        return images.OrderByDescending(i => i.CreatedAt).ToList();
    }

    public async Task<GeneratedImage?> FindByFileNameAsync(string fileName, CancellationToken ct = default)
    {
        var images = await _store.GetAllAsync(ct);
        return images.FirstOrDefault(i => i.FileName == fileName);
    }

    /// <summary>
    /// "a fluffy ginger cat" beats "df6cd015aaa44f34a966086d073051ff" in a downloads folder. Only
    /// letters, digits and single hyphens survive, so nothing here can steer the saved path.
    /// </summary>
    public string DownloadNameFor(GeneratedImage image)
    {
        var words = image.Prompt
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .Select(word => new string([.. word.Where(char.IsLetterOrDigit)]))
            .Where(word => word.Length > 0);

        var slug = string.Join('-', words).ToLowerInvariant();

        if (slug.Length > 60)
            slug = slug[..60].TrimEnd('-');

        return (slug.Length == 0 ? "image" : slug) + Path.GetExtension(image.FileName);
    }

    public async Task<bool> DeleteAsync(string imageId, CancellationToken ct = default)
    {
        if (await _store.GetAsync(imageId, ct) is not { } image)
            return false;

        // Record first: a file left behind is invisible, where a record pointing at a missing file
        // is a broken tile in the gallery.
        await _store.DeleteAsync(imageId, ct);

        var path = Path.Combine(GetImagesPath(), image.FileName);

        if (File.Exists(path))
            File.Delete(path);

        return true;
    }

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };
}
