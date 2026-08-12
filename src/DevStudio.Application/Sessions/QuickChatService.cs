using DevStudio.Application.Abstractions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;

namespace DevStudio.Application.Sessions;

public sealed class QuickChatService : IQuickChatService
{
    private const string QuickChatPrompt =
        "You are answering a direct question in a chat window. Reply conversationally and get to the " +
        "point. Do not explore the filesystem, run commands or edit anything unless you are explicitly " +
        "asked to.";

    private readonly IEntityStore<Agent> _agents;
    private readonly IProviderCliRegistry _clis;
    private readonly ISessionManager _sessions;

    public QuickChatService(IEntityStore<Agent> agents, IProviderCliRegistry clis, ISessionManager sessions)
    {
        _agents = agents;
        _clis = clis;
        _sessions = sessions;
    }

    public async Task<ChatSession> StartAsync(
        AiProvider provider,
        string? cliProviderId,
        string prompt,
        IReadOnlyList<string>? mcpServerIds = null,
        PermissionMode? permissionMode = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A prompt is required.", nameof(prompt));

        var agent = await GetOrCreateAgentAsync(provider, cliProviderId, ct);

        return await _sessions.StartAsync(new StartSessionRequest
        {
            AgentId = agent.Id,
            Prompt = prompt.Trim(),
            McpServerIds = mcpServerIds ?? [],
            // An explicit choice wins. Otherwise: plan mode refuses every MCP tool call, so a chat
            // with servers attached cannot be left in it.
            PermissionMode = permissionMode
                             ?? (mcpServerIds is { Count: > 0 } ? Domain.Agents.PermissionMode.AcceptEdits : null),
        }, ct);
    }

    private async Task<Agent> GetOrCreateAgentAsync(AiProvider provider, string? cliProviderId, CancellationToken ct)
    {
        var existing = (await _agents.GetAllAsync(ct))
            .FirstOrDefault(a => a.IsQuickChat && a.Provider == provider && a.CliProviderId == cliProviderId);

        if (existing is not null)
            return existing;

        // Name it after the CLI so the session list still reads sensibly.
        var cli = await _clis.ResolveAsync(provider, cliProviderId, ct);

        return await _agents.UpsertAsync(new Agent
        {
            Name = $"Quick chat · {cli.DisplayName}",
            Description = "Created for the quick chat window.",
            Provider = provider,
            CliProviderId = cliProviderId,
            // Read-only: a chat window should not be editing anything by surprise.
            PermissionMode = PermissionMode.Plan,
            UseWorktree = false,
            SystemPrompt = QuickChatPrompt,
            IsQuickChat = true,
        }, ct);
    }
}
