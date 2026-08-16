using DevStudio.Application.Common;
using DevStudio.Application.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Infrastructure.Sessions;

/// <summary>
/// Runs the session housekeeping on a slow timer: finishing conversations nobody has come back to,
/// then archiving finished ones that have gone stale. Both run once at start too, so a machine that
/// has been switched off for a week does not come back to a list of last week's conversations still
/// waiting for an answer.
/// </summary>
public sealed class SessionSweepHostedService : BackgroundService
{
    private readonly ISessionIdleCloser _idle;
    private readonly ISessionArchiver _archiver;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SessionSweepHostedService> _logger;

    public SessionSweepHostedService(
        ISessionIdleCloser idle,
        ISessionArchiver archiver,
        IOptions<OrchestratorOptions> options,
        ILogger<SessionSweepHostedService> logger)
    {
        _idle = idle;
        _archiver = archiver;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.SessionAutoArchiveHours <= 0 && _options.SessionIdleFinishHours <= 0)
        {
            _logger.LogInformation("Session housekeeping is off");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.SessionArchiveTickMinutes));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Idle first: a conversation finished by this tick is then old enough to be archived by
            // a later one, rather than waiting a whole extra round to be noticed.
            if (!await SweepAsync(_idle.SweepAsync, "idle-finish", stoppingToken))
                break;

            if (!await SweepAsync(_archiver.SweepAsync, "auto-archive", stoppingToken))
                break;

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                break;
        }
    }

    /// <summary>
    /// Runs one sweep. A sweep that throws must not take the timer down with it — the next tick is
    /// a better answer than never looking again — so only cancellation stops the loop.
    /// </summary>
    private async Task<bool> SweepAsync(
        Func<CancellationToken, Task<IReadOnlyList<Domain.Sessions.ChatSession>>> sweep,
        string name,
        CancellationToken ct)
    {
        try
        {
            await sweep(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {Sweep} sweep failed", name);
            return true;
        }
    }
}
