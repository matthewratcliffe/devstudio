using DevStudio.Application.Abstractions;
using DevStudio.Application.Globals;
using DevStudio.Domain.Globals;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Globals;

/// <summary>
/// Reads the shared variables out of the global settings. No caching of its own — the entity store
/// is already in memory — so an edit in Settings applies to the very next turn without a restart.
/// </summary>
public sealed class SharedEnvironment : ISharedEnvironment
{
    private readonly IEntityStore<GlobalSettings> _settings;
    private readonly ILogger<SharedEnvironment> _logger;

    public SharedEnvironment(IEntityStore<GlobalSettings> settings, ILogger<SharedEnvironment> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyDictionary<string, string>> ForLocalAsync(CancellationToken ct = default) =>
        ResolveAsync(remote: false, ct);

    public Task<IReadOnlyDictionary<string, string>> ForRemoteAsync(CancellationToken ct = default) =>
        ResolveAsync(remote: true, ct);

    private async Task<IReadOnlyDictionary<string, string>> ResolveAsync(bool remote, CancellationToken ct)
    {
        GlobalSettings? settings;
        try
        {
            settings = await _settings.GetAsync(GlobalSettings.WellKnownId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never the reason a turn cannot start: an unreadable settings file means no shared
            // variables, not no session.
            _logger.LogWarning(ex, "Could not read the shared environment; continuing without it");
            return Empty;
        }

        if (settings is null || settings.SharedEnvironment.Count == 0)
            return Empty;

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var variable in settings.SharedEnvironment)
        {
            if (!variable.Enabled || string.IsNullOrWhiteSpace(variable.Name))
                continue;

            if (remote && !variable.ShareWithRemote)
                continue;

            // Last one wins, so a duplicate name behaves the way the list reads rather than throwing.
            resolved[variable.Name.Trim()] = variable.Value;
        }

        return resolved;
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
