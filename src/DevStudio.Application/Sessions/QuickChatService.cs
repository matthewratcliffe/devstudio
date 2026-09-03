using DevStudio.Application.Abstractions;
using DevStudio.Application.Agents;
using DevStudio.Application.Remoting;
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
    private readonly IExecutionHostResolver _hosts;
    private readonly ISessionManager _sessions;
    private readonly IEntityStore<ChatSession> _sessionStore;

    public QuickChatService(
        IEntityStore<Agent> agents,
        IExecutionHostResolver hosts,
        ISessionManager sessions,
        IEntityStore<ChatSession> sessionStore)
    {
        _agents = agents;
        _hosts = hosts;
        _sessions = sessions;
        _sessionStore = sessionStore;
    }

    public async Task<ChatSession> StartAsync(
        AiProvider provider,
        string? cliProviderId,
        string prompt,
        IReadOnlyList<string>? mcpServerIds = null,
        PermissionMode? permissionMode = null,
        SessionModelSettings? model = null,
        TokenTactics? tokenMinimisation = null,
        string? remoteInstanceId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A prompt is required.", nameof(prompt));

        var agent = await GetOrCreateAgentAsync(provider, cliProviderId, remoteInstanceId, ct);

        return await _sessions.StartAsync(new StartSessionRequest
        {
            AgentId = agent.Id,
            Prompt = prompt.Trim(),
            McpServerIds = mcpServerIds ?? [],
            Model = model,
            TokenMinimisation = tokenMinimisation,
            RemoteInstanceId = remoteInstanceId,
            // An explicit choice wins. Otherwise: plan mode refuses every MCP tool call, so a chat
            // with servers attached cannot be left in it.
            PermissionMode = permissionMode
                             ?? (mcpServerIds is { Count: > 0 } ? Domain.Agents.PermissionMode.AcceptEdits : null),
        }, ct);
    }

    public async Task<ChatSession> SwitchProviderAsync(
        string sessionId,
        AiProvider provider,
        string? cliProviderId,
        CancellationToken ct = default)
    {
        // The live instance when there is one, so the change lands on the object the chat is
        // rendering and the next turn reads.
        var session = await _sessions.GetAsync(sessionId, ct)
                      ?? throw new InvalidOperationException("That session no longer exists.");

        var current = await _agents.GetAsync(session.AgentId, ct);
        if (current is not null && !current.IsQuickChat)
            throw new InvalidOperationException(
                $"'{current.Name}' is configured to use {current.Provider}. Change the CLI on the agent, or start a quick chat.");

        if (session.Provider == provider && session.CliProviderId == cliProviderId)
            return session;

        // Stays on the machine it started on. The workspace is over there and so is the CLI's own
        // conversation, so a switch that also moved machines would be a new chat wearing this one's
        // transcript.
        var agent = await GetOrCreateAgentAsync(provider, cliProviderId, session.RemoteInstanceId, ct);
        var host = await _hosts.ResolveAsync(session.RemoteInstanceId, ct);
        var cli = await host.Clis.ResolveAsync(provider, cliProviderId, ct);

        session.AgentId = agent.Id;
        session.AgentName = agent.Name;
        session.Provider = provider;
        session.CliProviderId = cliProviderId;
        session.CliProviderName = provider == AiProvider.Custom ? cli.DisplayName : null;

        // The id identifies a conversation inside the CLI being left behind; handing it to another one
        // is at best ignored and at worst an error, so the next turn starts fresh.
        session.ProviderSessionId = null;

        await _sessionStore.UpsertAsync(session, ct);

        return session;
    }

    private async Task<Agent> GetOrCreateAgentAsync(
        AiProvider provider,
        string? cliProviderId,
        string? remoteInstanceId,
        CancellationToken ct)
    {
        // Keyed by the machine as well as the CLI. A user-defined CLI's id is only meaningful on the
        // instance that defined it, so without this a quick chat on the desk machine could match the
        // hidden agent made for an unrelated local CLI that happened to share an id.
        var existing = (await _agents.GetAllAsync(ct))
            .FirstOrDefault(a => a.IsQuickChat &&
                                 a.Provider == provider &&
                                 a.CliProviderId == cliProviderId &&
                                 a.RemoteInstanceId == remoteInstanceId);

        if (existing is not null)
            return existing;

        // Name it after the CLI so the session list still reads sensibly.
        var host = await _hosts.ResolveAsync(remoteInstanceId, ct);
        var cli = await host.Clis.ResolveAsync(provider, cliProviderId, ct);

        return await _agents.UpsertAsync(new Agent
        {
            Name = $"Quick chat · {cli.DisplayName}",
            Description = "Created for the quick chat window.",
            Provider = provider,
            CliProviderId = cliProviderId,
            RemoteInstanceId = remoteInstanceId,
            // Read-only: a chat window should not be editing anything by surprise.
            PermissionMode = PermissionMode.Plan,
            UseWorktree = false,
            SystemPrompt = QuickChatPrompt,
            IsQuickChat = true,
        }, ct);
    }
}
