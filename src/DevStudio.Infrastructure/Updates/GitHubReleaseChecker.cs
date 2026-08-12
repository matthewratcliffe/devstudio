using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Updates;

/// <summary>
/// Asks GitHub for the newest release and compares it with the running build.
///
/// The desktop builds do not use this — they have Velopack, which downloads and installs. A
/// container cannot replace itself, so all this does is put a line in the UI saying a newer image
/// exists. It is off with one setting, for an install that should not be reaching the internet.
/// </summary>
public sealed class GitHubReleaseChecker : IReleaseChecker
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<GitHubReleaseChecker> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _current;

    private ReleaseStatus? _cached;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;

    public GitHubReleaseChecker(
        HttpClient http,
        IOptions<OrchestratorOptions> options,
        ILogger<GitHubReleaseChecker> logger,
        string? currentVersion = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        // The parameter exists so this is testable without publishing a versioned build.
        _current = currentVersion is null ? RunningVersion() : ReleaseVersion.Parse(currentVersion)?.ToString();
    }

    public async Task<ReleaseStatus> CheckAsync(CancellationToken ct = default)
    {
        if (!_options.UpdateCheckEnabled || string.IsNullOrWhiteSpace(_options.UpdateRepository))
            return ReleaseStatus.None(_current);

        // A build with no version cannot be compared against anything, and a development build being
        // told it is out of date every six hours is noise.
        if (_current is null)
            return ReleaseStatus.None(null);

        if (_cached is not null && DateTimeOffset.UtcNow - _checkedAt < CacheFor)
            return _cached;

        await _gate.WaitAsync(ct);

        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _checkedAt < CacheFor)
                return _cached;

            _cached = await FetchAsync(ct);
            _checkedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ReleaseStatus> FetchAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_options.UpdateRepository.Trim('/')}/releases/latest";
            var release = await _http.GetFromJsonAsync<GitHubRelease>(url, ct);

            if (release?.TagName is null || release.Draft)
                return ReleaseStatus.None(_current);

            // A stable install is not told about release candidates.
            if (release.PreRelease || ReleaseVersion.IsPreRelease(release.TagName))
                return ReleaseStatus.None(_current);

            return ReleaseVersion.IsNewer(_current, release.TagName)
                ? new ReleaseStatus(_current, release.TagName.TrimStart('v', 'V'), release.HtmlUrl)
                : ReleaseStatus.None(_current);
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, or a private repository. None of that is worth an error in the
            // UI; the next check is in six hours.
            _logger.LogDebug(ex, "Update check against {Repository} did not complete", _options.UpdateRepository);
            return ReleaseStatus.None(_current);
        }
    }

    /// <summary>The version stamped at publish time, which is the release tag in a CI build.</summary>
    private static string? RunningVersion()
    {
        // This assembly rather than the entry one: they are versioned together, and the entry
        // assembly is the desktop shell when the shell is what started the server.
        var informational = typeof(GitHubReleaseChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return ReleaseVersion.Parse(informational)?.ToString();
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool PreRelease);
}
