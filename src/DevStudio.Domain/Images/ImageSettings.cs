using DevStudio.Domain.Common;

namespace DevStudio.Domain.Images;

/// <summary>
/// Credentials and defaults for the image backends. A single record, edited on the Logins page and
/// held on the volume with everything else — the same place the CLI accounts live, because to an
/// operator these are the same kind of thing: what this app is allowed to sign in to.
/// </summary>
public sealed class ImageSettings : Entity
{
    /// <summary>Fixed id, so the settings are always found without a lookup.</summary>
    public const string WellKnownId = "images";

    /// <summary>Backend used when a caller does not name one.</summary>
    public ImageBackend DefaultBackend { get; set; } = ImageBackend.Pollinations;

    /// <summary>Upper bound on either dimension, so a bad prompt cannot ask for a gigapixel.</summary>
    public int MaxDimension { get; set; } = 2048;

    /// <summary>How long to wait for an image. Generous: a cold queue can take a while.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    public PollinationsSettings Pollinations { get; set; } = new();
    public CloudflareImageSettings Cloudflare { get; set; } = new();
    public GeminiImageSettings Gemini { get; set; } = new();

    public ImageSettings() => Id = WellKnownId;
}

public sealed class PollinationsSettings
{
    public string BaseUrl { get; set; } = "https://image.pollinations.ai";

    /// <summary>
    /// Optional. Anonymous works, at one request every fifteen seconds and with a watermark; a free
    /// token from auth.pollinations.ai lifts both. Use a server-side key (sk_), not a publishable one.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    public string Model { get; set; } = "flux";

    /// <summary>Seconds held between requests, to stay inside the tier's rate limit.</summary>
    public int MinSecondsBetweenRequests { get; set; } = 15;

    public List<string> Models { get; set; } = ["flux", "turbo"];
}

public sealed class CloudflareImageSettings
{
    public string AccountId { get; set; } = string.Empty;

    /// <summary>API token carrying the Workers AI permission.</summary>
    public string ApiToken { get; set; } = string.Empty;

    public string Model { get; set; } = "@cf/black-forest-labs/flux-1-schnell";

    public List<string> Models { get; set; } =
    [
        "@cf/black-forest-labs/flux-1-schnell",
        "@cf/stabilityai/stable-diffusion-xl-base-1.0",
    ];
}

public sealed class GeminiImageSettings
{
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";

    /// <summary>AI Studio key. The free tier covers the flash image model; the pro one needs billing.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gemini-2.5-flash-image";

    public List<string> Models { get; set; } = ["gemini-2.5-flash-image", "gemini-3-pro-image-preview"];
}
