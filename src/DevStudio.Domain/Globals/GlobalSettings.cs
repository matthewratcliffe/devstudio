using DevStudio.Domain.Common;

namespace DevStudio.Domain.Globals;

/// <summary>
/// The house rules: setup notes, coding standards and reference files that apply to every agent in
/// every project. A single record — the store holds exactly one, under <see cref="WellKnownId"/>.
/// Projects layer their own instructions on top, and can opt out entirely.
/// </summary>
public sealed class GlobalSettings : Entity
{
    /// <summary>Fixed id so the settings are always found without a lookup by name.</summary>
    public const string WellKnownId = "global";

    /// <summary>
    /// Standing instructions prepended to the system prompt of every session — conventions, the
    /// stack, what to avoid, how to commit.
    /// </summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// Standards imported from the team settings repository, applied above <see cref="Instructions"/>
    /// so the local ones read as the narrower rule. Rewritten by every sync, which is why they are
    /// kept apart rather than merged into the field somebody edits by hand.
    /// </summary>
    public string TeamInstructions { get; set; } = string.Empty;

    /// <summary>
    /// Reference material staged into every workspace as <c>./global-files</c>: a setup guide, a
    /// coding standards document, an architecture note.
    /// </summary>
    public List<StoredFile> Files { get; set; } = [];

    public GlobalSettings() => Id = WellKnownId;
}
