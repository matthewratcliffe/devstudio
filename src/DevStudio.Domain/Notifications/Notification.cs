using DevStudio.Domain.Common;

namespace DevStudio.Domain.Notifications;

/// <summary>
/// A message shown to everyone using the app. There is no per-user targeting or read state — one
/// shared list, and dismissing it removes it for every viewer, not just the one who clicked.
/// </summary>
public sealed class Notification : Entity
{
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>info, ok, warn, err — same vocabulary as the toast kinds.</summary>
    public string Kind { get; set; } = "info";

    /// <summary>Who raised it: "mcp", "agent:&lt;name&gt;", or a signed-in username.</summary>
    public string CreatedBy { get; set; } = "manual";

    /// <summary>Set when the notification is about a specific session, so a viewer can jump to it.</summary>
    public string? SessionId { get; set; }
}
