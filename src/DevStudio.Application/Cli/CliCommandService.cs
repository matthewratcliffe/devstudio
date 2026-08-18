using System.Text;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Notifications;
using DevStudio.Application.Queues;
using DevStudio.Application.Sessions;
using DevStudio.Application.Workflows;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Notifications;
using DevStudio.Domain.Queues;
using DevStudio.Domain.Sessions;
using DevStudio.Domain.Workflows;

namespace DevStudio.Application.Cli;

public sealed class CliCommandService : ICliCommandService
{
    private const int MaxHistory = 100;
    private static readonly TimeSpan ForegroundTimeout = TimeSpan.FromMinutes(20);

    private readonly IEntityStore<Agent> _agents;
    private readonly IEntityStore<Workflow> _workflows;
    private readonly ISessionManager _sessions;
    private readonly IWorkflowEngine _engine;
    private readonly IQueueService _queues;
    private readonly INotificationService _notifications;

    private readonly object _historyLock = new();
    private readonly List<(string Command, TerminalResult Result, DateTimeOffset At)> _history = [];

    public CliCommandService(
        IEntityStore<Agent> agents,
        IEntityStore<Workflow> workflows,
        ISessionManager sessions,
        IWorkflowEngine engine,
        IQueueService queues,
        INotificationService notifications)
    {
        _agents = agents;
        _workflows = workflows;
        _sessions = sessions;
        _engine = engine;
        _queues = queues;
        _notifications = notifications;
    }

    public async Task<TerminalResult> ExecuteAsync(string commandLine, string invokedBy, CancellationToken ct = default)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.Length == 0)
            return new TerminalResult([]);

        var tokens = Tokenize(trimmed);
        var background = tokens.RemoveAll(t => t is "-b" or "--background") > 0;

        var noun = tokens[0].ToLowerInvariant();
        var verb = tokens.Count > 1 ? tokens[1].ToLowerInvariant() : string.Empty;
        var rest = tokens.Skip(2).ToList();

        TerminalResult result;
        try
        {
            result = noun switch
            {
                "help" or "?" => Help(),
                "clear" or "cls" => new TerminalResult([], ClearScreen: true),
                "outputs" or "history" => Outputs(verb),
                "sessions" => await SessionsAsync(verb, rest, background, ct),
                "agents" => await AgentsAsync(verb, rest, background, invokedBy, ct),
                "workflows" => await WorkflowsAsync(verb, rest, background, invokedBy, ct),
                "queues" => await QueuesAsync(verb, rest, ct),
                "notifications" => await NotificationsAsync(verb, rest, invokedBy, ct),
                _ => TerminalResult.Of($"Unknown command '{noun}'. Type 'help' for what's available.", isError: true),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = TerminalResult.Of(ex.Message, isError: true);
        }

        // "outputs"/"history" reads the log rather than adding to it, and clearing the screen is a
        // display action with nothing worth remembering.
        if (noun is not ("outputs" or "history" or "clear" or "cls"))
        {
            lock (_historyLock)
            {
                _history.Add((trimmed, result, DateTimeOffset.UtcNow));
                if (_history.Count > MaxHistory)
                    _history.RemoveAt(0);
            }
        }

        return result;
    }

    private TerminalResult Outputs(string verb)
    {
        lock (_historyLock)
        {
            if (_history.Count == 0)
                return TerminalResult.Of("No commands run yet this session.");

            if (verb is "last" or "")
            {
                var (command, result, at) = _history[^1];
                var lines = new List<string> { $"[{at.ToLocalTime():HH:mm:ss}] > {command}" };
                lines.AddRange(result.Lines.Select(l => l.Text));
                return TerminalResult.Of(lines);
            }

            if (verb == "all")
            {
                var lines = new List<string>();
                foreach (var (command, result, at) in _history)
                {
                    lines.Add($"[{at.ToLocalTime():HH:mm:ss}] > {command}");
                    lines.AddRange(result.Lines.Select(l => l.Text));
                    lines.Add(string.Empty);
                }

                return TerminalResult.Of(lines);
            }

            return TerminalResult.Of("Usage: outputs [last|all]", isError: true);
        }
    }

    // -- sessions -------------------------------------------------------------------------------

    private async Task<TerminalResult> SessionsAsync(string verb, List<string> args, bool background, CancellationToken ct)
    {
        switch (verb)
        {
            case "list" or "":
            {
                var status = OptionValue(args, "--status");
                var all = await _sessions.GetAllAsync(ct);
                var filtered = all
                    .Where(s => !s.IsArchived)
                    .Where(s => status is null || string.Equals(s.Status.ToString(), status, StringComparison.OrdinalIgnoreCase))
                    .Take(50)
                    .ToList();

                if (filtered.Count == 0)
                    return TerminalResult.Of("No sessions.");

                return TerminalResult.Of(filtered.Select(s =>
                    $"{s.Id}  [{s.Status}]  {s.AgentName}  {s.Title}"));
            }

            case "get" or "show":
            {
                var id = RequirePositional(args, 0, "sessions get <id>");
                var session = await _sessions.GetAsync(id, ct)
                    ?? throw new InvalidOperationException($"No session '{id}'.");

                var lines = new List<string>
                {
                    $"{session.Title}  [{session.Status}]",
                    $"agent={session.AgentName}  dir={session.WorkingDirectory}  turns={session.TurnCount}",
                };
                lines.AddRange(session.Messages.TakeLast(5).Select(m => $"[{m.Role}] {Truncate(m.Content, 300)}"));
                return TerminalResult.Of(lines);
            }

            case "delete" or "rm":
            {
                var id = RequirePositional(args, 0, "sessions delete <id>");
                var deleted = await _sessions.DeleteAsync(id, ct);
                return TerminalResult.Of(deleted ? $"Deleted session {id}." : $"No session '{id}'.", isError: !deleted);
            }

            case "cancel" or "stop":
            {
                var id = RequirePositional(args, 0, "sessions cancel <id>");
                await _sessions.CancelAsync(id);
                return TerminalResult.Of($"Cancelled session {id}.");
            }

            case "send":
            {
                var id = RequirePositional(args, 0, "sessions send <id> <message>");
                var message = string.Join(' ', args.Skip(1));
                if (message.Length == 0)
                    throw new InvalidOperationException("Usage: sessions send <id> <message>");

                await _sessions.SendAsync(id, message, ct);
                return TerminalResult.Of("Sent.");
            }

            case "start":
            {
                var agentRef = RequirePositional(args, 0, "sessions start <agent> <prompt> [-b]");
                var prompt = string.Join(' ', args.Skip(1));
                if (prompt.Length == 0)
                    throw new InvalidOperationException("Usage: sessions start <agent> <prompt> [-b]");

                var agent = await ResolveAgentAsync(agentRef, ct);
                return await StartSessionAsync(agent, prompt, background, ct);
            }

            default:
                return TerminalResult.Of($"Unknown 'sessions {verb}'. Try: list, get, delete, cancel, send, start.", isError: true);
        }
    }

    private async Task<TerminalResult> StartSessionAsync(Agent agent, string prompt, bool background, CancellationToken ct)
    {
        var request = new StartSessionRequest { AgentId = agent.Id, Prompt = prompt };

        if (background)
        {
            var started = await _sessions.StartAsync(request, ct);
            return TerminalResult.Of($"Started session {started.Id} in the background. Watch it on /sessions/{started.Id}.");
        }

        var finished = await _sessions.RunToCompletionAsync(request, ForegroundTimeout, ct);
        var lines = new List<string> { $"{finished.Title}  [{finished.Status}]" };
        var lastAgentMessage = finished.Messages.LastOrDefault(m => m.Role == MessageRole.Agent);
        if (lastAgentMessage is not null)
            lines.Add(Truncate(lastAgentMessage.Content, 1000));

        return new TerminalResult(lines.Select(l => new TerminalLine(l)).ToList(), false);
    }

    // -- agents -----------------------------------------------------------------------------------

    private async Task<TerminalResult> AgentsAsync(string verb, List<string> args, bool background, string invokedBy, CancellationToken ct)
    {
        switch (verb)
        {
            case "list" or "":
            {
                var all = await _agents.GetAllAsync(ct);
                if (all.Count == 0)
                    return TerminalResult.Of("No agents configured.");

                return TerminalResult.Of(all.Select(a =>
                    $"{a.Id}  {a.Name}  provider={a.Provider}  enabled={a.Enabled}"));
            }

            case "run":
            {
                var agentRef = RequirePositional(args, 0, "agents run <agent> <prompt> [-b]");
                var prompt = string.Join(' ', args.Skip(1));
                if (prompt.Length == 0)
                    throw new InvalidOperationException("Usage: agents run <agent> <prompt> [-b]");

                var agent = await ResolveAgentAsync(agentRef, ct);
                return await StartSessionAsync(agent, prompt, background, ct);
            }

            default:
                return TerminalResult.Of($"Unknown 'agents {verb}'. Try: list, run.", isError: true);
        }
    }

    // -- workflows --------------------------------------------------------------------------------

    private async Task<TerminalResult> WorkflowsAsync(string verb, List<string> args, bool background, string invokedBy, CancellationToken ct)
    {
        switch (verb)
        {
            case "list" or "":
            {
                var all = await _workflows.GetAllAsync(ct);
                if (all.Count == 0)
                    return TerminalResult.Of("No workflows configured.");

                return TerminalResult.Of(all.Select(w =>
                    $"{w.Id}  {w.Name}  steps={w.Steps.Count}  inputs=[{string.Join(", ", w.Inputs.Select(i => i.Name))}]  enabled={w.Enabled}"));
            }

            case "run":
            {
                var workflowRef = RequirePositional(args, 0, "workflows run <workflow> [key=value...] [-b]");
                var workflow = await ResolveWorkflowAsync(workflowRef, ct);

                var inputs = new Dictionary<string, string>();
                foreach (var pair in args.Skip(1))
                {
                    var separator = pair.IndexOf('=');
                    if (separator > 0)
                        inputs[pair[..separator]] = pair[(separator + 1)..];
                }

                if (background)
                {
                    var started = await _engine.StartAsync(workflow.Id, inputs, invokedBy, ct);
                    return TerminalResult.Of($"Started run {started.Id} in the background. Watch it on /workflows.");
                }

                var run = await _engine.RunAsync(workflow.Id, inputs, invokedBy, ct);
                var lines = new List<string> { $"{run.WorkflowName}  [{run.Status}]" };
                lines.AddRange(run.Steps.Select(s =>
                    $"  {s.StepName}  [{s.Status}]" + (s.Error is { Length: > 0 } error ? $"  error={error}" : string.Empty)));

                if (run.Error is { Length: > 0 } runError)
                    lines.Add($"error: {runError}");

                return TerminalResult.Of(lines);
            }

            default:
                return TerminalResult.Of($"Unknown 'workflows {verb}'. Try: list, run.", isError: true);
        }
    }

    // -- queues -----------------------------------------------------------------------------------

    private async Task<TerminalResult> QueuesAsync(string verb, List<string> args, CancellationToken ct)
    {
        switch (verb)
        {
            case "list" or "":
            {
                var all = await _queues.GetQueuesAsync(ct);
                if (all.Count == 0)
                    return TerminalResult.Of("No queues configured.");

                var lines = new List<string>();
                foreach (var queue in all)
                {
                    var counts = await _queues.GetCountsAsync(queue.Id, ct);
                    lines.Add($"{queue.Id}  {queue.Name}  pending={counts.Pending}  running={counts.Running}  failed={counts.Failed}");
                }

                return TerminalResult.Of(lines);
            }

            case "enqueue" or "add":
            {
                var queueRef = RequirePositional(args, 0, "queues enqueue <queue> <title>");
                var title = string.Join(' ', args.Skip(1));
                if (title.Length == 0)
                    throw new InvalidOperationException("Usage: queues enqueue <queue> <title>");

                var target = await _queues.ResolveQueueAsync(queueRef, ct)
                    ?? throw new InvalidOperationException($"No queue '{queueRef}'.");

                var result = await _queues.EnqueueAsync(new EnqueueRequest
                {
                    QueueId = target.Id,
                    Title = title,
                    Body = title,
                    EnqueuedBy = "terminal",
                }, ct);

                return TerminalResult.Of(result.Accepted
                    ? $"Queued as {result.Item!.Id} on '{target.Name}'."
                    : result.Reason);
            }

            default:
                return TerminalResult.Of($"Unknown 'queues {verb}'. Try: list, enqueue.", isError: true);
        }
    }

    // -- notifications ------------------------------------------------------------------------------

    private async Task<TerminalResult> NotificationsAsync(string verb, List<string> args, string invokedBy, CancellationToken ct)
    {
        switch (verb)
        {
            case "list" or "":
            {
                var all = await _notifications.GetAllAsync(ct);
                if (all.Count == 0)
                    return TerminalResult.Of("No active notifications.");

                return TerminalResult.Of(all.Select(n => $"{n.Id}  [{n.Kind}]  {n.Title}"));
            }

            case "create" or "add":
            {
                var title = string.Join(' ', args);
                if (title.Length == 0)
                    throw new InvalidOperationException("Usage: notifications create <title>");

                var notification = await _notifications.CreateAsync(title, string.Empty, "info", invokedBy, ct);
                return TerminalResult.Of($"Raised notification {notification.Id}.");
            }

            case "dismiss" or "rm":
            {
                var id = RequirePositional(args, 0, "notifications dismiss <id>");
                var dismissed = await _notifications.DismissAsync(id, ct);
                return TerminalResult.Of(dismissed ? "Dismissed." : $"No notification '{id}'.", isError: !dismissed);
            }

            case "clear":
            {
                var count = await _notifications.DismissAllAsync(ct);
                return TerminalResult.Of($"Cleared {count} notification(s).");
            }

            default:
                return TerminalResult.Of($"Unknown 'notifications {verb}'. Try: list, create, dismiss, clear.", isError: true);
        }
    }

    // -- shared helpers -----------------------------------------------------------------------------

    private async Task<Agent> ResolveAgentAsync(string idOrName, CancellationToken ct)
    {
        var all = await _agents.GetAllAsync(ct);
        var wanted = idOrName.Trim();

        var match = all.FirstOrDefault(a => string.Equals(a.Id, wanted, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(a => string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(a => a.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException(
            all.Count == 0
                ? $"No agent '{wanted}'. No agents are configured."
                : $"No agent '{wanted}'. Agents: {string.Join(", ", all.Select(a => a.Name))}.");
    }

    private async Task<Workflow> ResolveWorkflowAsync(string idOrName, CancellationToken ct)
    {
        var all = await _workflows.GetAllAsync(ct);
        var wanted = idOrName.Trim();

        var match = all.FirstOrDefault(w => string.Equals(w.Id, wanted, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => string.Equals(w.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException(
            all.Count == 0
                ? $"No workflow '{wanted}'. No workflows are configured."
                : $"No workflow '{wanted}'. Workflows: {string.Join(", ", all.Select(w => w.Name))}.");
    }

    private static string RequirePositional(List<string> args, int index, string usage) =>
        index < args.Count ? args[index] : throw new InvalidOperationException($"Usage: {usage}");

    private static string? OptionValue(List<string> args, string name)
    {
        var index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>Splits on whitespace, honouring single or double quotes so a prompt can contain spaces.</summary>
    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '"';

        foreach (var c in input)
        {
            if (inQuotes)
            {
                if (c == quoteChar)
                    inQuotes = false;
                else
                    sb.Append(c);
            }
            else if (c is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
            tokens.Add(sb.ToString());

        return tokens;
    }

    private static TerminalResult Help() => TerminalResult.Of(
    [
        "devStudio terminal — drive the app from the command line. Append -b to a long-running",
        "command (sessions start, agents run, workflows run) to background it and get the id",
        "back immediately instead of waiting for it to finish.",
        "",
        "sessions list [--status <status>]      List sessions.",
        "sessions get <id>                      Show a session's status and recent messages.",
        "sessions delete <id>                   Delete a session.",
        "sessions cancel <id>                   Stop a running session.",
        "sessions send <id> <message>            Queue a turn on an existing session.",
        "sessions start <agent> <prompt> [-b]    Start a session with an agent.",
        "",
        "agents list                             List configured agents.",
        "agents run <agent> <prompt> [-b]        Start a session with an agent (alias of sessions start).",
        "",
        "workflows list                          List configured workflows.",
        "workflows run <workflow> [k=v...] [-b]  Run a workflow, optionally with inputs.",
        "",
        "queues list                             List queues and their counts.",
        "queues enqueue <queue> <title>          Add an item to a queue.",
        "",
        "notifications list                      List active notifications.",
        "notifications create <title>            Raise a notification.",
        "notifications dismiss <id>              Dismiss one notification.",
        "notifications clear                     Dismiss every notification.",
        "",
        "outputs [last|all]                      Show previous command output (default: last).",
        "clear                                   Clear the screen.",
        "help                                    Show this list.",
    ]);
}
