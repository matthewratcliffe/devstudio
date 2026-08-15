using DevStudio.Application.Abstractions;
using DevStudio.Application.Common;
using DevStudio.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Application.Sessions;

/// <summary>
/// Archives conversations that have finished and gone stale, so the sessions list keeps describing
/// current work rather than everything the machine has ever run.
/// </summary>
public interface ISessionArchiver
{
    /// <summary>
    /// Archives every finished session older than the configured age. Returns the sessions it took,
    /// which is empty when the sweep is switched off or there was nothing old enough.
    /// </summary>
    Task<IReadOnlyList<ChatSession>> SweepAsync(CancellationToken ct = default);
}

public sealed class SessionArchiver : ISessionArchiver
{
    private readonly ISessionManager _sessions;
    private readonly IEntityStore<ChatSession> _store;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SessionArchiver> _logger;

    public SessionArchiver(
        ISessionManager sessions,
        IEntityStore<ChatSession> store,
        IOptions<OrchestratorOptions> options,
        ILogger<SessionArchiver> logger)
    {
        _sessions = sessions;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ChatSession>> SweepAsync(CancellationToken ct = default)
    {
        var hours = _options.SessionAutoArchiveHours;
        if (hours <= 0)
            return [];

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(hours);
        var all = await _sessions.GetAllAsync(ct);

        var stale = all
            .Where(s => !s.IsArchived)
            // Restored by hand, so it stays out of the archive whatever its age.
            .Where(s => !s.AutoArchiveExempt)
            // Still working, or waiting on an answer, which is current work however old the session is.
            .Where(s => !s.IsLive)
            .Where(s => LastActivity(s) < cutoff)
            .ToList();

        var archived = new List<ChatSession>();

        foreach (var session in stale)
        {
            ct.ThrowIfCancellationRequested();

            session.IsArchived = true;

            try
            {
                await _store.UpsertAsync(session, ct);
                archived.Add(session);
            }
            catch (Exception ex)
            {
                // Put it back, or the list would show it archived until the next reload contradicts that.
                session.IsArchived = false;
                _logger.LogWarning(ex, "Could not auto-archive session {SessionId}", session.Id);
            }
        }

        if (archived.Count > 0)
            _logger.LogInformation("Auto-archived {Count} sessions idle for more than {Hours}h", archived.Count, hours);

        return archived;
    }

    /// <summary>
    /// When the session last did anything. <see cref="ChatSession.EndedAt"/> is the honest answer for
    /// a run that stopped; sessions that never started — a queued one abandoned at Pending — only
    /// have the timestamps every entity carries.
    /// </summary>
    private static DateTimeOffset LastActivity(ChatSession session) =>
        session.EndedAt ?? session.UpdatedAt;
}
