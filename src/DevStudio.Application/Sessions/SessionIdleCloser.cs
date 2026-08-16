using DevStudio.Application.Common;
using DevStudio.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevStudio.Application.Sessions;

/// <summary>
/// Finishes conversations nobody has come back to. A chat left open is a chat that can still be
/// restarted hours later against a workspace that has moved on underneath it, and one abandoned
/// mid-thought reads as current work in every list it appears in.
/// </summary>
public interface ISessionIdleCloser
{
    /// <summary>
    /// Finishes every open conversation idle for longer than the configured window, and returns
    /// them. Empty when the sweep is switched off or nothing has been quiet long enough.
    /// </summary>
    Task<IReadOnlyList<ChatSession>> SweepAsync(CancellationToken ct = default);
}

public sealed class SessionIdleCloser : ISessionIdleCloser
{
    private readonly ISessionManager _sessions;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SessionIdleCloser> _logger;

    public SessionIdleCloser(
        ISessionManager sessions,
        IOptions<OrchestratorOptions> options,
        ILogger<SessionIdleCloser> logger)
    {
        _sessions = sessions;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ChatSession>> SweepAsync(CancellationToken ct = default)
    {
        var hours = _options.SessionIdleFinishHours;
        if (hours <= 0)
            return [];

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(hours);
        var all = await _sessions.GetAllAsync(ct);

        var idle = all
            .Where(s => !s.IsClosed)
            // An agent still at work is not idle, however long the turn has been running. Waiting
            // on a person is not the same thing: that is exactly the state being swept up here.
            .Where(s => !s.IsWorking)
            .Where(s => LastSaidAnything(s) < cutoff)
            .ToList();

        var finished = new List<ChatSession>();

        foreach (var session in idle)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await _sessions.CloseAsync(session.Id, Reason(hours), ct) is { } closed)
                    finished.Add(closed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not finish idle session {SessionId}", session.Id);
            }
        }

        if (finished.Count > 0)
            _logger.LogInformation("Finished {Count} conversations idle for more than {Hours}h", finished.Count, hours);

        return finished;
    }

    private static string Reason(int hours) =>
        $"No input for {hours} hour{(hours == 1 ? "" : "s")}.";

    /// <summary>
    /// When the conversation last moved. The final line of the transcript is the honest answer —
    /// saving a note or renaming the session is housekeeping, not conversation, and would otherwise
    /// keep an abandoned chat looking alive. Sessions with nothing said in them yet fall back to the
    /// timestamps every entity carries.
    /// </summary>
    private static DateTimeOffset LastSaidAnything(ChatSession session) =>
        session.Messages.Count > 0
            ? session.Messages[^1].Timestamp
            : session.EndedAt ?? session.UpdatedAt;
}
