using System.Collections.Concurrent;
using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Domain.Providers;
using Microsoft.Extensions.Options;

namespace AiShop.Infrastructure.SourceControl;

/// <summary>
/// Caches the configured forge hostnames. The store is the source of truth; configuration provides
/// the defaults, so an unset host still works out of the box.
/// </summary>
public sealed class SourceControlHosts : ISourceControlHosts
{
    private readonly ConcurrentDictionary<SourceControlProvider, string> _overrides = new();
    private readonly IEntityStore<SourceControlSettings> _store;
    private readonly OrchestratorOptions _options;

    public SourceControlHosts(IEntityStore<SourceControlSettings> store, IOptions<OrchestratorOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public string Get(SourceControlProvider provider) =>
        _overrides.TryGetValue(provider, out var host) && !string.IsNullOrWhiteSpace(host)
            ? host
            : Default(provider);

    public bool IsOverridden(SourceControlProvider provider) => _overrides.ContainsKey(provider);

    public async Task SetAsync(SourceControlProvider provider, string? host, CancellationToken ct = default)
    {
        // Store the normalised form so the file and the UI never disagree.
        var cleaned = Normalise(host);

        var settings = await _store.GetAsync(SourceControlSettings.WellKnownId, ct) ?? new SourceControlSettings();
        settings.SetHost(provider, cleaned);
        await _store.UpsertAsync(settings, ct);

        if (cleaned is null)
            _overrides.TryRemove(provider, out _);
        else
            _overrides[provider] = cleaned;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var settings = await _store.GetAsync(SourceControlSettings.WellKnownId, ct);
        if (settings is null)
            return;

        foreach (var provider in Enum.GetValues<SourceControlProvider>())
        {
            if (Normalise(settings.GetHost(provider)) is { } host)
                _overrides[provider] = host;
        }
    }

    private string Default(SourceControlProvider provider) => provider switch
    {
        SourceControlProvider.GitLab => string.IsNullOrWhiteSpace(_options.GitLabHost) ? "gitlab.com" : _options.GitLabHost.Trim(),
        _ => string.IsNullOrWhiteSpace(_options.GitHubHost) ? "github.com" : _options.GitHubHost.Trim(),
    };

    /// <summary>Accepts a bare host or a URL, and keeps only the host part.</summary>
    private static string? Normalise(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var trimmed = host.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return trimmed.TrimEnd('/');
    }
}
