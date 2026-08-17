using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;

namespace DevStudio.Application.Sessions;

/// <summary>
/// A small, deterministic nudge towards a provider's built-in slash command.
/// It is intentionally conservative: a hint should be useful for the current problem, not turn
/// every transcript into a command menu.
/// </summary>
public sealed record CommandHint(string Command, string Purpose, string DocumentationUrl);

public static class CommandHints
{
    private const string ClaudeDocs = "https://code.claude.com/docs/en/commands";
    private const string CodexDocs = "https://developers.openai.com/codex/cli/slash-commands";

    public static CommandHint? For(ChatSession session)
    {
        if (session.Provider is not (AiProvider.Claude or AiProvider.Codex) ||
            session.Status is SessionStatus.Starting or SessionStatus.Running)
            return null;

        var recent = string.Join("\n", session.Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Agent or MessageRole.Tool)
            .TakeLast(8)
            .Select(m => m.Content));

        if (ContainsAny(recent, "permission denied", "approval denied", "permission prompt", "allowlist", "not allowed"))
            return Hint(session, "/permissions", "adjust tool permissions for this task");

        if (ContainsAny(recent, "review the diff", "review current diff", "review the current diff", "review my changes", "review the code", "code review", "before we merge"))
            return Hint(session, session.Provider == AiProvider.Claude ? "/code-review" : "/review", "review the current changes for bugs and cleanup");

        if (ContainsAny(recent, "error", "exception", "stack trace", "traceback", "failing test", "tests fail", "doesn't work", "does not work", "broken", "regression"))
            return Hint(session, session.Provider == AiProvider.Claude ? "/debug" : "/debug-config", "diagnose the problem with focused diagnostics");

        if (ContainsAny(recent, "make a plan", "what approach", "which approach", "break this down", "large change", "big change", "not sure how to start"))
            return Hint(session, "/plan", "switch to a focused planning workflow before changing code");

        if (ContainsAny(recent, "context window", "out of context", "too much context", "conversation is long", "history is long"))
            return session.Provider == AiProvider.Claude
                ? Hint(session, "/compact", "summarise the conversation to free context")
                : null;

        return null;
    }

    private static CommandHint Hint(ChatSession session, string command, string purpose) =>
        new(command, purpose, session.Provider == AiProvider.Claude ? ClaudeDocs : CodexDocs);

    private static bool ContainsAny(string text, params string[] phrases) =>
        phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
