using DevStudio.Application.Teams;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevStudio.Infrastructure.Teams;

/// <summary>
/// Imports the team settings repository on start, so a machine that has been switched off catches up
/// with what the team changed without anybody remembering to press a button.
///
/// It runs in the background rather than blocking startup: a sync pulls over the network, and the app
/// should come up whether or not that works.
/// </summary>
public sealed class TeamSyncHostedService : BackgroundService
{
    private readonly ITeamSettingsService _teams;
    private readonly ILogger<TeamSyncHostedService> _logger;

    public TeamSyncHostedService(ITeamSettingsService teams, ILogger<TeamSyncHostedService> logger)
    {
        _teams = teams;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var settings = await _teams.GetAsync(stoppingToken);

            if (!settings.SyncOnStart || string.IsNullOrWhiteSpace(settings.RepositoryId))
                return;

            var result = await _teams.SyncAsync(stoppingToken);

            if (result.Succeeded)
                _logger.LogInformation("Team settings synced: {Message}", result.Message);
            else
                _logger.LogWarning("Team settings sync failed: {Message}", result.Message);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-sync is not a failure worth reporting.
        }
    }
}
