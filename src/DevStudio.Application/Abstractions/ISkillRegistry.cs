namespace DevStudio.Application.Abstractions;

/// <summary>One hit from the registry's search index. Descriptions are not indexed, so a result
/// carries only enough to choose between candidates — the rest arrives with the download.</summary>
public sealed record SkillSearchResult(string Source, string Slug, string Name, int Installs)
{
    /// <summary>owner/repo/slug, the triple skills.sh and its CLI both use to name a skill.</summary>
    public string Id => $"{Source}/{Slug}";
}

/// <summary>A file that came with a skill, at a path relative to the skill's own folder.</summary>
public sealed record SkillFile(string Path, string Contents);

/// <summary>
/// A skill as the registry serves it: the SKILL.md split into frontmatter and body, plus every
/// supporting file it references.
/// </summary>
public sealed record SkillPackage(
    string Source,
    string Slug,
    string Name,
    string Description,
    string Content,
    IReadOnlyList<string> Tags,
    IReadOnlyList<SkillFile> Files,
    string Hash);

/// <summary>Read access to a public skill registry. Nothing here writes to the orchestrator.</summary>
public interface ISkillRegistry
{
    /// <summary>Human-readable name of the registry, for the UI to attribute results to.</summary>
    string Name { get; }

    Task<IReadOnlyList<SkillSearchResult>> SearchAsync(string query, int limit = 20, CancellationToken ct = default);

    /// <summary>Downloads a skill in full. Null when the registry does not have it.</summary>
    Task<SkillPackage?> FetchAsync(string source, string slug, CancellationToken ct = default);
}

/// <summary>What a pull did: the stored skill, and whether it differed from what was already here.</summary>
public sealed record SkillImportResult(Domain.Skills.Skill Skill, bool Changed);

/// <summary>Pulls skills out of a registry and into the orchestrator's own library.</summary>
public interface ISkillImporter
{
    /// <summary>
    /// Pulls a skill and stores it. A second pull of the same source updates the skill already
    /// there rather than making a duplicate.
    /// </summary>
    Task<SkillImportResult> ImportAsync(string source, string slug, CancellationToken ct = default);

    /// <summary>Discards the supporting files a pull left on the volume.</summary>
    Task DeleteBundleAsync(string skillId, CancellationToken ct = default);
}
