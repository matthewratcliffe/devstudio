using DevStudio.Application.Abstractions;
using DevStudio.Domain.Images;

namespace DevStudio.Infrastructure.Images;

/// <summary>
/// Holds the image settings in memory so a backend can answer "am I configured?" during a render
/// without going to disk. The cache is refreshed whenever they are saved, and again before every
/// generation, so an edit on the Logins page takes effect on the next image rather than the next
/// restart.
/// </summary>
public sealed class ImageSettingsService : IImageSettingsService
{
    private readonly IEntityStore<ImageSettings> _store;
    private volatile ImageSettings _current = new();

    public ImageSettingsService(IEntityStore<ImageSettings> store) => _store = store;

    public ImageSettings Current => _current;

    public async Task<ImageSettings> LoadAsync(CancellationToken ct = default)
    {
        // Nothing saved yet is the normal first-run state, and the defaults are usable — Pollinations
        // needs no credentials — so this is not an error worth surfacing.
        _current = await _store.GetAsync(ImageSettings.WellKnownId, ct) ?? new ImageSettings();
        return _current;
    }

    public async Task SaveAsync(ImageSettings settings, CancellationToken ct = default)
    {
        settings.Id = ImageSettings.WellKnownId;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        await _store.UpsertAsync(settings, ct);
        _current = settings;
    }
}
