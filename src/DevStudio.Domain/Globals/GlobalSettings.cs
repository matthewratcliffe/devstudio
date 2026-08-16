using DevStudio.Domain.Common;
using DevStudio.Domain.Agents;

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

    /// <summary>Whether agent and browser workspace paths must stay inside their workspace.</summary>
    public bool ValidateWorkspacePaths { get; set; } = true;

    /// <summary>
    /// Whether existing symlinks may be resolved while validating workspace paths. Even when this
    /// is enabled, a resolved target must still remain inside the workspace.
    /// </summary>
    public bool FollowWorkspaceSymlinks { get; set; }

    /// <summary>
    /// Token minimisation tactics preselected for newly created agents and quick chats. Existing
    /// agents keep their own selection, and a chat can still override it.
    /// </summary>
    public TokenTactics DefaultTokenMinimisation { get; set; } = TokenTacticsDefaults.Recommended;

    public GlobalSettings() => Id = WellKnownId;
}
