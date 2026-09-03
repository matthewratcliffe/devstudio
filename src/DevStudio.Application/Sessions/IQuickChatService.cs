using DevStudio.Application.Agents;
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
        SessionModelSettings? model = null,
        TokenTactics? tokenMinimisation = null,
        // The machine the CLI runs on. Null is this one.
        string? remoteInstanceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Points an existing quick chat at a different CLI from the next turn on. The transcript stays
    /// where it is, but the provider's own conversation id cannot: it belongs to the CLI being left
    /// behind, so the new one starts from the rolling summary instead of the other's history.
    ///
    /// Refused for a session belonging to a configured agent — that agent's CLI is part of what it
    /// is, and changing it silently for one conversation would make the agent a lie.
    /// </summary>
    Task<ChatSession> SwitchProviderAsync(
        string sessionId,
        AiProvider provider,
        string? cliProviderId,
        CancellationToken ct = default);
}
