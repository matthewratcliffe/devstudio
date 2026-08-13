namespace DevStudio.Infrastructure.Skills;

/// <summary>
/// Where a pulled skill's supporting files live. They sit on the volume rather than in the skill
/// entity because the JSON store is read and written whole, and a single skill's reference tree can
/// run to hundreds of kilobytes.
/// </summary>
internal static class SkillBundle
{
    /// <summary>
    /// Deliberately not under the store's own "skills" folder: that directory is the skill
    /// collection, and mixing bundle directories into it leaves the two owning the same namespace.
    /// </summary>
    public static string PathFor(string dataPath, string skillId) =>
        Path.Combine(dataPath, "skill-bundles", skillId);
}
