using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Skills;

/// <summary>
/// Turns a registry download into a skill in the library: SKILL.md into the entity, everything it
/// references onto the volume beside it.
/// </summary>
public sealed class SkillImporter : ISkillImporter
{
    private readonly ISkillRegistry _registry;
    private readonly IEntityStore<Skill> _skills;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SkillImporter> _logger;

    public SkillImporter(
        ISkillRegistry registry,
        IEntityStore<Skill> skills,
        IOptions<OrchestratorOptions> options,
        ILogger<SkillImporter> logger)
    {
        _registry = registry;
        _skills = skills;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SkillImportResult> ImportAsync(string source, string slug, CancellationToken ct = default)
    {
        var package = await _registry.FetchAsync(source, slug, ct)
            ?? throw new InvalidOperationException($"{_registry.Name} has no skill {source}/{slug}.");

        var id = $"{package.Source}/{package.Slug}";
        var existing = (await _skills.GetAllAsync(ct))
            .FirstOrDefault(s => string.Equals(s.RegistrySource, id, StringComparison.OrdinalIgnoreCase));

        // An empty hash means the registry did not tell us what it served, so we cannot claim the
        // pull was a no-op — treat it as changed and write it again.
        var changed = existing is null
                      || string.IsNullOrEmpty(package.Hash)
                      || !string.Equals(existing.RegistryHash, package.Hash, StringComparison.Ordinal);

        if (!changed)
            return new SkillImportResult(existing!, false);

        var skill = existing ?? new Skill();
        skill.Name = package.Name;
        skill.Slug = string.IsNullOrWhiteSpace(skill.Slug) ? package.Slug : skill.Slug;
        skill.Description = package.Description;
        skill.Content = package.Content;
        skill.RegistrySource = id;
        skill.RegistryHash = package.Hash;
        skill.BundleFileCount = package.Files.Count;

        if (package.Tags.Count > 0)
            skill.Tags = [.. package.Tags];

        // Upsert first: the bundle is keyed by the entity id, which a new skill does not have yet.
        var stored = await _skills.UpsertAsync(skill, ct);
        await WriteBundleAsync(stored.Id, package.Files, ct);

        _logger.LogInformation(
            "Pulled skill {Source} from {Registry} with {Count} supporting file(s)",
            id, _registry.Name, package.Files.Count);

        return new SkillImportResult(stored, true);
    }

    public Task DeleteBundleAsync(string skillId, CancellationToken ct = default)
    {
        var path = SkillBundle.PathFor(_options.DataPath, skillId);

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the bundled files for skill {Skill}", skillId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Replaces the bundle wholesale. A re-pull that renamed or dropped a file would otherwise
    /// leave the old one behind, and SKILL.md would keep pointing at content the author removed.
    /// </summary>
    private async Task WriteBundleAsync(string skillId, IReadOnlyList<SkillFile> files, CancellationToken ct)
    {
        var path = SkillBundle.PathFor(_options.DataPath, skillId);

        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        if (files.Count == 0)
            return;

        Directory.CreateDirectory(path);

        foreach (var file in files)
        {
            var destination = Path.GetFullPath(Path.Combine(path, file.Path));

            // Belt and braces over the registry's own path check: only write inside the bundle.
            if (!destination.StartsWith(Path.GetFullPath(path) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                _logger.LogWarning("Skipped bundled file {Path} — it resolves outside the skill folder", file.Path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, file.Contents, ct);
        }
    }
}
