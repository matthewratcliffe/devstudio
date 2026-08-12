using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;

namespace DevStudio.Application.Sessions;

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
