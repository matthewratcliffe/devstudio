using DevStudio.Domain.Providers;

namespace DevStudio.Application.Sessions;

public sealed record CliSlashCommand(string Name, string Description, string Usage);

/// <summary>Built-in slash commands understood by the two CLIs devStudio drives.</summary>
public static class CliSlashCommands
{
    private static readonly IReadOnlyList<CliSlashCommand> Claude =
    [
        new("/add-dir", "add a working directory for this session", "/add-dir <path>"),
        new("/agents", "manage subagents", "/agents"),
        new("/background", "run the current work in the background", "/background"),
        new("/branch", "branch this conversation", "/branch <name>"),
        new("/btw", "ask a side question without adding to the chat", "/btw <question>"),
        new("/clear", "start a fresh conversation", "/clear"),
        new("/code-review", "review the current changes", "/code-review"),
        new("/compact", "summarise the conversation to free context", "/compact"),
        new("/config", "change Claude Code settings", "/config"),
        new("/context", "inspect context usage", "/context"),
        new("/debug", "diagnose a runtime problem", "/debug"),
        new("/diff", "inspect the current diff", "/diff"),
        new("/doctor", "check Claude Code setup", "/doctor"),
        new("/effort", "change reasoning effort", "/effort [level]"),
        new("/export", "export the conversation", "/export [filename]"),
        new("/fork", "copy this conversation into a background session", "/fork [name]"),
        new("/help", "show command help", "/help [command]"),
        new("/init", "create a project guide", "/init"),
        new("/mcp", "manage MCP servers", "/mcp"),
        new("/memory", "manage project memory", "/memory"),
        new("/model", "switch model", "/model [name]"),
        new("/permissions", "manage tool permissions", "/permissions"),
        new("/plan", "enter plan mode", "/plan"),
        new("/resume", "resume an earlier conversation", "/resume [conversation]"),
        new("/rewind", "rewind to a checkpoint", "/rewind"),
        new("/security-review", "review changes for security issues", "/security-review"),
        new("/status", "show session status", "/status"),
    ];

    private static readonly IReadOnlyList<CliSlashCommand> Codex =
    [
        new("/approval", "change approval mode", "/approval [mode]"),
        new("/apps", "manage app connectors", "/apps"),
        new("/clear", "clear conversation history", "/clear"),
        new("/debug-config", "inspect configuration diagnostics", "/debug-config"),
        new("/diff", "show the current diff", "/diff"),
        new("/feedback", "send Codex feedback", "/feedback <message>"),
        new("/fork", "fork this conversation", "/fork"),
        new("/help", "show command help", "/help [command]"),
        new("/model", "switch model", "/model [name]"),
        new("/new", "start a new conversation", "/new"),
        new("/permissions", "change permissions", "/permissions"),
        new("/personality", "change communication style", "/personality [style]"),
        new("/reset", "reset conversation and session state", "/reset"),
        new("/resume", "resume a saved conversation", "/resume [conversation]"),
        new("/review", "review the working tree", "/review"),
        new("/sandbox", "change sandbox mode", "/sandbox [mode]"),
        new("/side", "start a side conversation", "/side"),
        new("/skills", "browse and use skills", "/skills"),
        new("/status", "show session status", "/status"),
        new("/usage", "show token usage", "/usage"),
        new("/use", "load a skill", "/use <skill>"),
    ];

    public static IReadOnlyList<CliSlashCommand> For(AiProvider provider, string? draft)
    {
        var commands = provider == AiProvider.Claude ? Claude : provider == AiProvider.Codex ? Codex : [];
        return Filter(commands, draft);
    }

    /// <summary>
    /// Narrows an arbitrary command list (e.g. a CLI's live-queried commands) to what matches the
    /// draft, the same way <see cref="For"/> narrows the built-in lists.
    /// </summary>
    public static IReadOnlyList<CliSlashCommand> Filter(IReadOnlyList<CliSlashCommand> commands, string? draft)
    {
        var query = Query(draft);

        if (query is null)
            return [];

        return [.. commands
            .Where(command => command.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(8)];
    }

    /// <summary>Only offer completion for a command at the start of the message.</summary>
    private static string? Query(string? draft)
    {
        if (string.IsNullOrWhiteSpace(draft))
            return null;

        var text = draft.TrimStart();
        if (!text.StartsWith('/') || text.Contains('\n') || text.Contains('\r'))
            return null;

        var token = text.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries)[0];
        return token.Length == 1 || token.StartsWith('/') ? token : null;
    }
}
