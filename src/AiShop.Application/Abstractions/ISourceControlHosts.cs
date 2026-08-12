using AiShop.Domain.Providers;

namespace AiShop.Application.Abstractions;

/// <summary>
/// The hostname each forge CLI talks to. Cached in memory so the CLI adapters can read it without
/// an await in the middle of building a command; configuration supplies the fallback.
/// </summary>
public interface ISourceControlHosts
{
    /// <summary>Configured host, or the built-in default for that forge.</summary>
    string Get(SourceControlProvider provider);

    /// <summary>Whether the host has been overridden from the UI.</summary>
    bool IsOverridden(SourceControlProvider provider);

    Task SetAsync(SourceControlProvider provider, string? host, CancellationToken ct = default);

    /// <summary>Reads stored settings into the cache. Called once at startup.</summary>
    Task LoadAsync(CancellationToken ct = default);
}
