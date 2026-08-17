using DevStudio.Application.Abstractions;
using DevStudio.Application.Globals;
using DevStudio.Domain.Globals;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Globals;

/// <summary>
/// Keeps the standards files repository up to date without anybody pressing a button: once on start,
/// and again on a fixed interval for the rest of the process's life. A failed sync is logged and
/// never rethrown — a network blip must not take the app down or block anything waiting on it.
/// </summary>
public sealed class StandardsFilesSyncHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IStandardsFilesSyncService _sync;
    private readonly IEntityStore<GlobalSettings> _globals;
    private readonly ILogger<StandardsFilesSyncHostedService> _logger;

    public StandardsFilesSyncHostedService(
        IStandardsFilesSyncService sync,
        IEntityStore<GlobalSettings> globals,
        ILogger<StandardsFilesSyncHostedService> logger)
    {
        _sync = sync;
        _globals = globals;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down between runs is not a failure worth reporting.
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _globals.GetAsync(GlobalSettings.WellKnownId, ct);
            if (string.IsNullOrWhiteSpace(settings?.FilesRepositoryId))
                return;

            var result = await _sync.SyncAsync(ct);

            if (result.Succeeded)
                _logger.LogInformation("Standards files synced: {Message}", result.Message);
            else
                _logger.LogWarning("Standards files sync failed: {Message}", result.Message);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-sync is not a failure worth reporting.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standards files sync failed unexpectedly");
        }
    }
}
