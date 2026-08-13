using DevStudio.Domain.Common;

namespace DevStudio.Domain.Skills;

/// <summary>
/// A reusable instruction file. Materialised into the session workspace as
/// .claude/skills/{slug}/SKILL.md (and the codex equivalent) before the CLI starts.
/// </summary>
public sealed class Skill : Entity
{
    public string Name { get; set; } = "New skill";
    /// <summary>Directory name used when the skill is written to disk.</summary>
    public string Slug { get; set; } = string.Empty;
    /// <summary>One line telling the model when to reach for this skill.</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Markdown body of SKILL.md, without frontmatter — that is generated.</summary>
    public string Content { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path, relative to the team settings repository, of the file this was imported from. Set means
    /// the repository owns it: the next sync rewrites it, and deleting the file deletes it. Null is a
    /// local definition, which no sync ever touches.
    /// </summary>
    public string? TeamSourcePath { get; set; }

    /// <summary>
    /// Where this was pulled from, as owner/repo/slug — the same triple skills.sh and the CLI use to
    /// name a skill. Set means re-pulling is possible; null is a hand-written or team skill.
    /// </summary>
    public string? RegistrySource { get; set; }

    /// <summary>
    /// Content hash the registry returned for the pull that produced <see cref="Content"/> and the
    /// bundled files. Compared against a fresh download to tell an update from a no-op.
    /// </summary>
    public string? RegistryHash { get; set; }

    /// <summary>
    /// How many supporting files came with the pull. They live outside this entity, on the volume,
    /// because a skill's reference tree can run to hundreds of kilobytes and the store is read
    /// whole every time. Zero means SKILL.md was the entire skill.
    /// </summary>
    public int BundleFileCount { get; set; }
}
