using DevStudio.Application.Common;
using DevStudio.Application.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Sessions;

/// <summary>
/// Runs the age-based session sweep on a slow timer, including once at start so a machine that has
/// been switched off for a week does not come back to a list of last week's conversations.
/// </summary>
public sealed class SessionArchiveHostedService : BackgroundService
{
    private readonly ISessionArchiver _archiver;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SessionArchiveHostedService> _logger;

    public SessionArchiveHostedService(
        ISessionArchiver archiver,
        IOptions<OrchestratorOptions> options,
        ILogger<SessionArchiveHostedService> logger)
    {
        _archiver = archiver;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.SessionAutoArchiveHours <= 0)
        {
            _logger.LogInformation("Session auto-archive is off");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.SessionArchiveTickMinutes));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _archiver.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session auto-archive sweep failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                break;
        }
    }
}
