using System.Reflection;

namespace DevStudio.Desktop;

/// <summary>
/// What build this is, from the assembly rather than from Velopack.
///
/// Velopack knows the version too, but only for a copy the installer put there: a portable build
/// and a build run from the repository both come back empty, and those are exactly the copies most
/// likely to be swapped underneath a profile that outlives them.
/// </summary>
public static class DesktopVersion
{
    /// <summary>What an unstamped build calls itself. CI never produces this version.</summary>
    private const string Unstamped = "1.0.0";

    /// <summary>
    /// The release this build was stamped with, or <c>development</c> for one nobody gave a version
    /// to. Stable for the life of a release, which is what makes it safe to name a directory after.
    /// </summary>
    public static string Current { get; } = Stamped() ?? "development";

    /// <summary>
    /// The same thing, except that a development build is distinguished by when it was compiled.
    /// A release is replaced by installing another one; a development build is replaced by pressing
    /// build, and the version does not move when it is.
    /// </summary>
    public static string BuildStamp { get; } = Stamped() ?? $"development-{CompiledAt():yyyyMMddHHmmss}";

    private static string? Stamped()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(DesktopVersion).Assembly;

        // The informational version carries the commit after a '+' when SourceLink is in play, and
        // the assembly version pads to four parts. Neither belongs in the answer.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0].Trim() ?? assembly.GetName().Version?.ToString(3);

        return string.IsNullOrWhiteSpace(version) || version == Unstamped ? null : version;
    }

    private static DateTime CompiledAt()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(DesktopVersion).Assembly;
            return File.GetLastWriteTimeUtc(assembly.Location);
        }
        catch
        {
            // A single-file or trimmed build has no file to ask about. Nothing is lost but the
            // ability to notice a rebuild, which is only ever a development concern anyway.
            return DateTime.UnixEpoch;
        }
    }
}
