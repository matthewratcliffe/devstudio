using AiShop.Domain.Common;

namespace AiShop.Domain.Skills;

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
}
