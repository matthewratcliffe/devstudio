using DevStudio.Application.Abstractions;
using DevStudio.Domain.Notifications;

namespace DevStudio.Application.Notifications;

public sealed class NotificationService : INotificationService
{
    private readonly IEntityStore<Notification> _store;

    public NotificationService(IEntityStore<Notification> store)
    {
        _store = store;
    }

    public event Action? Changed;

    public async Task<IReadOnlyList<Notification>> GetAllAsync(CancellationToken ct = default)
    {
        var all = await _store.GetAllAsync(ct);
        return all.OrderByDescending(n => n.CreatedAt).ToList();
    }

    public async Task<Notification> CreateAsync(string title, string body, string kind, string createdBy, string? sessionId = null, CancellationToken ct = default)
    {
        var notification = new Notification
        {
            Title = title.Trim(),
            Body = body.Trim(),
            Kind = string.IsNullOrWhiteSpace(kind) ? "info" : kind.Trim(),
            CreatedBy = createdBy,
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
        };

        await _store.UpsertAsync(notification, ct);
        Changed?.Invoke();
        return notification;
    }

    public async Task<bool> DismissAsync(string id, CancellationToken ct = default)
    {
        var removed = await _store.DeleteAsync(id, ct);
        if (removed)
            Changed?.Invoke();

        return removed;
    }

    public async Task<int> DismissAllAsync(CancellationToken ct = default)
    {
        var all = await _store.GetAllAsync(ct);
        foreach (var notification in all)
            await _store.DeleteAsync(notification.Id, ct);

        if (all.Count > 0)
            Changed?.Invoke();

        return all.Count;
    }
}
