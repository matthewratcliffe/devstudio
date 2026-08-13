using System.Text.Json;
using System.Text.Json.Serialization;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Teams;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Skills;

/// <summary>
/// skills.sh, the public registry the `npx skills` CLI installs from. Two endpoints do everything:
/// /api/search ranks skills by install count, and /api/download returns a skill's whole folder as
/// JSON. Talking to them directly keeps this off a Node runtime the container does not have.
/// </summary>
public sealed class SkillsShRegistry : ISkillRegistry
{
    private const string SkillFileName = "SKILL.md";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly ILogger<SkillsShRegistry> _logger;

    public SkillsShRegistry(HttpClient client, ILogger<SkillsShRegistry> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => "skills.sh";

    public async Task<IReadOnlyList<SkillSearchResult>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var url = $"api/search?q={Uri.EscapeDataString(query.Trim())}&limit={limit}";
        var response = await GetAsync<SearchResponse>(url, ct);

        return response?.Skills is null
            ? []
            : [.. response.Skills
                .Where(s => !string.IsNullOrWhiteSpace(s.Source) && !string.IsNullOrWhiteSpace(s.SkillId))
                .Select(s => new SkillSearchResult(s.Source!, s.SkillId!, s.Name ?? s.SkillId!, s.Installs))];
    }

    public async Task<SkillPackage?> FetchAsync(string source, string slug, CancellationToken ct = default)
    {
        var parts = source.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            _logger.LogWarning("Skill source {Source} is not owner/repo", source);
            return null;
        }

        var url = $"api/download/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/{Uri.EscapeDataString(slug)}";
        var download = await GetAsync<DownloadResponse>(url, ct);

        var files = download?.Files?.Where(f => f.Path is not null && f.Contents is not null).ToList();
        if (files is null || files.Count == 0)
            return null;

        // The manifest is the skill; everything else is what it reads. Without it there is nothing
        // to install, whatever else the download contained.
        var manifest = files.FirstOrDefault(f =>
            string.Equals(f.Path, SkillFileName, StringComparison.OrdinalIgnoreCase));

        if (manifest is null)
        {
            _logger.LogWarning("Skill {Source}/{Slug} downloaded without a {File}", source, slug, SkillFileName);
            return null;
        }

        var (front, body) = TeamDefinitions.ReadFrontmatter(manifest.Contents!);

        // The frontmatter name is the skill's real identity — folder names drift from it, which is
        // why the registry's slug and the repository's directory often disagree.
        var name = Value(front, "name") ?? slug;
        var description = Value(front, "description") ?? string.Empty;

        var supporting = files
            .Where(f => f != manifest)
            .Select(f => new SkillFile(f.Path!, f.Contents!))
            .Where(f => IsSafeRelativePath(f.Path))
            .ToList();

        return new SkillPackage(
            Source: source,
            Slug: slug,
            Name: name,
            Description: description,
            Content: body,
            Tags: TeamDefinitions.ReadList(Value(front, "tags")),
            Files: supporting,
            Hash: download!.Hash ?? string.Empty);
    }

    /// <summary>
    /// These paths come off the public internet and are about to become file names, so anything
    /// that could escape the skill's own folder is dropped rather than sanitised into something
    /// that still writes.
    /// </summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':'))
            return false;

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0
               && segments.All(s => s != "." && s != ".." && s.Trim().Length > 0)
               && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Registry} returned {Status} for {Url}", Name, (int)response.StatusCode, url);
                return default;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Could not reach {Registry} for {Url}", Name, url);
            return default;
        }
    }

    private static string? Value(IReadOnlyDictionary<string, string> front, string key) =>
        front.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private sealed record SearchResponse([property: JsonPropertyName("skills")] List<SearchHit>? Skills);

    private sealed record SearchHit(string? SkillId, string? Name, string? Source, int Installs);

    private sealed record DownloadResponse(List<DownloadFile>? Files, string? Hash);

    private sealed record DownloadFile(string? Path, string? Contents);
}
