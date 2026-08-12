namespace DevStudio.Domain.Images;

/// <summary>
/// Which service turns a prompt into pixels. Ordered by how much setup they need: Pollinations works
/// with no account at all, the other two want a key before they will answer.
/// </summary>
public enum ImageBackend
{
    /// <summary>Keyless and free. Throttled hard when anonymous, and watermarks unless registered.</summary>
    Pollinations = 0,

    /// <summary>Workers AI. Needs an account id and token; 10,000 neurons a day come free.</summary>
    Cloudflare = 1,

    /// <summary>Gemini image models. Needs an AI Studio key, and is the only one of the three that edits.</summary>
    Gemini = 2,
}
