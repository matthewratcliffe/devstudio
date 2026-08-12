namespace DevStudio.Application.Abstractions;

/// <param name="Current">Version of the running build, or null when it was built without one.</param>
/// <param name="Latest">Newest published release, when the check succeeded.</param>
/// <param name="Url">Where to read about it and get it.</param>
public sealed record ReleaseStatus(string? Current, string? Latest, string? Url)
{
    public bool UpdateAvailable => Latest is not null;

    public static ReleaseStatus None(string? current) => new(current, null, null);
}

/// <summary>
/// Says whether a newer release has been published. The container cannot update itself — pulling a
/// new image is the operator's call — so this exists to tell somebody, not to act.
/// </summary>
public interface IReleaseChecker
{
    /// <summary>
    /// Cached; safe to call on every page render. Never throws — an unreachable GitHub means
    /// "nothing to report", not a broken UI.
    /// </summary>
    Task<ReleaseStatus> CheckAsync(CancellationToken ct = default);
}
