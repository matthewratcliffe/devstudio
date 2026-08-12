using AiShop.Domain.Agents;
using AiShop.Domain.Providers;
using AiShop.Domain.Sessions;

namespace AiShop.Application.Sessions;

/// <summary>
/// Starts a conversation with nothing configured first — no project, no agent. Behind the scenes it
/// reuses one hidden agent per provider so accounts, guidance and summarisation all still apply.
/// </summary>
public interface IQuickChatService
{
    Task<ChatSession> StartAsync(
        AiProvider provider,
        string? cliProviderId,
        string prompt,
        IReadOnlyList<string>? mcpServerIds = null,
        PermissionMode? permissionMode = null,
        CancellationToken ct = default);
}
