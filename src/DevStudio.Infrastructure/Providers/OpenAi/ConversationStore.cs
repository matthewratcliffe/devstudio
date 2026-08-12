using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace DevStudio.Infrastructure.Providers.OpenAi;

/// <summary>
/// Message history for HTTP-backed sessions. The CLIs keep their own conversation and are handed
/// back a session id to resume; a plain endpoint remembers nothing, so the history lives here.
/// It is deliberately in memory only: a restart drops it, and the next turn starts a fresh
/// conversation rather than resuming a half-remembered one.
/// </summary>
public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, List<JsonObject>> _conversations = new();

    /// <summary>The conversation so far, or an empty one if this is a new or forgotten session.</summary>
    public List<JsonObject> Get(string? sessionId) =>
        sessionId is not null && _conversations.TryGetValue(sessionId, out var messages)
            ? messages
            : [];

    public void Set(string sessionId, List<JsonObject> messages) => _conversations[sessionId] = messages;

    public void Forget(string sessionId) => _conversations.TryRemove(sessionId, out _);
}
