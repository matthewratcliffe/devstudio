using System.Globalization;

namespace DevStudio.Application.Common;

/// <summary>
/// Compares the running build against the newest published release.
///
/// Kept separate from anything that talks to GitHub, because the interesting part is the edge cases:
/// a development build with no version at all, a tag written <c>v1.4.2</c>, and a pre-release that
/// should never look newer than the stable version it precedes.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>
    /// Parses a released version. Returns null for anything that is not one, including the
    /// placeholder <c>1.0.0</c> the SDK stamps on a build nobody gave a version to — treating that
    /// as real would tell every development build that an update is available.
    /// </summary>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();

        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        // Informational versions carry build metadata (1.4.2+abc123) and pre-release tags
        // (1.5.0-rc.1); neither belongs in the comparison.
        foreach (var separator in new[] { '+', '-' })
        {
            var index = trimmed.IndexOf(separator);
            if (index > 0)
                trimmed = trimmed[..index];
        }

        if (!Version.TryParse(trimmed, out var parsed))
        {
            // A bare major version is still a version.
            return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                ? new Version(major, 0, 0)
                : null;
        }

        var normalised = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));

        return normalised == new Version(1, 0, 0) || normalised == new Version(0, 0, 0) ? null : normalised;
    }

    /// <summary>
    /// True when <paramref name="latest"/> is a real release newer than the running one. Unknown on
    /// either side means no: an unversioned build has nothing meaningful to compare, and nagging it
    /// about updates it cannot apply helps nobody.
    /// </summary>
    public static bool IsNewer(string? current, string? latest)
    {
        var running = Parse(current);
        var published = Parse(latest);

        return running is not null && published is not null && published > running;
    }

    /// <summary>A pre-release tag is published, but it is not what a stable install should be told about.</summary>
    public static bool IsPreRelease(string? value) =>
        value is not null && value.Contains('-', StringComparison.Ordinal);
}
