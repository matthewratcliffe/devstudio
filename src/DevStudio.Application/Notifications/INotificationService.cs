using DevStudio.Domain.Notifications;

namespace DevStudio.Application.Notifications;

/// <summary>
/// Owns the shared notification list. There is no per-user state: everyone sees the same list, and
/// dismissing one removes it for every viewer, which is why dismiss is a delete rather than a flag.
/// </summary>
public interface INotificationService
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<Notification>> GetAllAsync(CancellationToken ct = default);

    Task<Notification> CreateAsync(string title, string body, string kind, string createdBy, string? sessionId = null, CancellationToken ct = default);

    /// <summary>Removes one notification for everyone.</summary>
    Task<bool> DismissAsync(string id, CancellationToken ct = default);

    /// <summary>Removes every notification for everyone.</summary>
    Task<int> DismissAllAsync(CancellationToken ct = default);

    /// <summary>Raised after any create or dismiss so every open circuit can refresh without polling.</summary>
    event Action? Changed;
}
