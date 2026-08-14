using System.Text.Json;
using System.Text.Json.Nodes;
using DevStudio.Application.Abstractions;
using DevStudio.Application.Queues;
using DevStudio.Application.Sessions;
using DevStudio.Application.Workflows;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Images;
using DevStudio.Domain.Projects;
using DevStudio.Domain.Queues;
using DevStudio.Domain.Sessions;
using DevStudio.Domain.Workflows;

namespace DevStudio.Ui.Endpoints;

/// <summary>
/// Exposes the orchestrator itself as an MCP server over streamable HTTP, so an agent can see what
/// other agents are doing: list sessions, read another session's transcript, leave notes, and kick
/// off more work. This is the other half of MCP support — agents also *consume* servers configured
/// on the MCP page.
/// </summary>
public static class McpEndpoint
{
    private const string ProtocolVersion = "2025-06-18";

    /// <summary>Tools exposed on the images-only surface. Deliberately one.</summary>
    private static readonly string[] ImageTools = ["generate_image"];

    public static IEndpointRouteBuilder MapMcp(this IEndpointRouteBuilder app)
    {
        Map(app, "/mcp", "devstudio", only: null);

        // A second surface carrying nothing but image generation. It is attached to every session by
        // default, and the whole point of it being separate is that "this chat can draw a picture"
        // should not also mean "this chat can start sessions and steer other agents".
        Map(app, "/mcp/images", "devstudio-images", ImageTools);

        return app;
    }

    private static void Map(
        IEndpointRouteBuilder app,
        string path,
        string serverName,
        IReadOnlyCollection<string>? only)
    {
        app.MapPost(path, async (HttpContext context, IServiceProvider services, CancellationToken ct) =>
        {
            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                return Results.Json(Error(null, -32700, $"Parse error: {ex.Message}"));
            }

            if (body is null)
                return Results.Json(Error(null, -32600, "Empty request."));

            // A batch is just a list of the same envelope.
            if (body is JsonArray batch)
            {
                var responses = new JsonArray();
                foreach (var item in batch)
                {
                    var response = await HandleAsync(item, services, serverName, only, ct);
                    if (response is not null)
                        responses.Add(response);
                }

                return responses.Count == 0 ? Results.NoContent() : Results.Json(responses);
            }

            var single = await HandleAsync(body, services, serverName, only, ct);
            return single is null ? Results.NoContent() : Results.Json(single);
        });

        // Some clients probe with GET before opening a stream; answer politely rather than 404.
        app.MapGet(path, () => Results.Json(new { name = serverName, protocolVersion = ProtocolVersion }));
    }

    private static async Task<JsonNode?> HandleAsync(
        JsonNode? message,
        IServiceProvider services,
        string serverName,
        IReadOnlyCollection<string>? only,
        CancellationToken ct)
    {
        if (message is not JsonObject request)
            return Error(null, -32600, "Invalid request.");

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();

        // Notifications carry no id and expect no response.
        if (id is null && method is not null && method.StartsWith("notifications/", StringComparison.Ordinal))
            return null;

        try
        {
            return method switch
            {
                "initialize" => Success(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = serverName,
                        ["version"] = "1.0.0",
                    },
                }),
                "ping" => Success(id, new JsonObject()),
                "tools/list" => Success(id, new JsonObject { ["tools"] = BuildToolList(only) }),
                "tools/call" => Success(id, await CallToolAsync(request["params"] as JsonObject, services, only, ct)),
                _ => Error(id, -32601, $"Unknown method '{method}'."),
            };
        }
        catch (Exception ex)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    /// <summary>
    /// The tool list, narrowed to <paramref name="only"/> when this surface is a restricted one.
    /// Filtering the advertisement is not the protection — <see cref="CallToolAsync"/> enforces the
    /// same set — but a tool a model cannot see is one it will not try.
    /// </summary>
    private static JsonArray BuildToolList(IReadOnlyCollection<string>? only)
    {
        if (only is null)
            return AllTools();

        var filtered = new JsonArray();

        foreach (var tool in AllTools())
        {
            if (tool?["name"]?.GetValue<string>() is { } name && only.Contains(name))
                filtered.Add(tool.DeepClone());
        }

        return filtered;
    }

    private static JsonArray AllTools() =>
    [
        Tool("list_agents", "List every configured agent with its provider and permission mode.", new JsonObject()),
        Tool("list_sessions", "List orchestrator sessions, newest first.", Props(
            ("status", "string", "Filter by status: Running, AwaitingInput, Completed, Failed, Cancelled."),
            ("projectId", "string", "Only sessions in this project."),
            ("limit", "number", "Maximum sessions to return (default 25)."))),
        Tool("get_session", "Read another session's full transcript, including tool calls.", Props(
            ("sessionId", "string", "Id of the session to read.")), "sessionId"),
        Tool("send_message", "Queue another user turn on an existing session.", Props(
            ("sessionId", "string", "Session to send to."),
            ("message", "string", "The prompt to send.")), "sessionId", "message"),
        Tool("start_session", "Start a new session with one of the configured agents.", Props(
            ("agentId", "string", "Agent to run."),
            ("prompt", "string", "First prompt."),
            ("projectId", "string", "Optional project to run inside.")), "agentId", "prompt"),
        Tool("send_guidance", "Steer a session that is already running: a course correction that reaches it mid-turn.", Props(
            ("sessionId", "string", "Session to steer."),
            ("guidance", "string", "What the agent should do differently."),
            ("interrupt", "boolean", "Stop the turn in flight so the steer applies immediately. Default false.")), "sessionId", "guidance"),
        Tool("check_guidance", "Pull any guidance waiting for you. Call this during long tasks — it is how a steer reaches you mid-turn.", Props(
            ("sessionId", "string", "Your own session id, given in your instructions.")), "sessionId"),
        Tool("get_notes", "Read the operator notes attached to a session.", Props(
            ("sessionId", "string", "Session to read notes from.")), "sessionId"),
        Tool("add_note", "Append a note to a session so humans and other agents can see it.", Props(
            ("sessionId", "string", "Session to annotate."),
            ("note", "string", "Text to append.")), "sessionId", "note"),
        Tool("list_projects", "List projects with their instructions and attached files.", new JsonObject()),
        Tool("list_workflows", "List runnable workflows and their declared inputs.", new JsonObject()),
        Tool("run_workflow", "Start a workflow run in the background.", Props(
            ("workflowId", "string", "Workflow to run."),
            ("inputs", "object", "Input values keyed by input name.")), "workflowId"),
        Tool("list_queues", "List the work queues, what processes each one, and how much is outstanding.", new JsonObject()),
        Tool("enqueue", "Add an item to a work queue. Agents are started for queued items automatically. Sending the same key twice is safe — a queue that deduplicates will say it already has it rather than doing the work again.", Props(
            ("queueId", "string", "Queue to add to, by id or by name as list_queues shows it."),
            ("key", "string", "Your identity for this work, e.g. the merge request URL. Send the same key for the same thing every time so it is not queued twice."),
            ("title", "string", "One line describing the item."),
            ("body", "string", "The detail, which the processing agent receives."),
            ("payload", "object", "Anything else worth passing on, as string values."),
            ("priority", "number", "Higher goes first. Default 0.")), "queueId"),
        Tool("list_queue_items", "List items on a queue with their status and outcome.", Props(
            ("queueId", "string", "Queue to read, by id or by name."),
            ("status", "string", "Filter: Pending, Running, Succeeded, Failed, Cancelled."),
            ("limit", "number", "Maximum items to return (default 25).")), "queueId"),
        Tool("generate_image", "Generate an image from a text description. Returns a URL this orchestrator serves it from.", Props(
            ("prompt", "string", "What to draw. Detailed prompts work better than short ones."),
            ("width", "number", "Pixels wide. Optional, default 1024."),
            ("height", "number", "Pixels tall. Optional, default 1024."),
            ("seed", "number", "Optional. The same seed and prompt reproduce the same image."),
            ("backend", "string", "Optional service: Pollinations, Cloudflare or Gemini. Defaults to the configured one."),
            ("sessionId", "string", "Optional. Your own session id, so the image is filed against this session.")), "prompt"),
    ];

    private static async Task<JsonObject> CallToolAsync(
        JsonObject? parameters,
        IServiceProvider services,
        IReadOnlyCollection<string>? only,
        CancellationToken ct)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
        var arguments = parameters?["arguments"] as JsonObject ?? [];

        // A restricted surface refuses what it does not advertise. Without this, knowing the tool
        // names would be enough to reach the full set through the narrow endpoint.
        if (only is not null && !only.Contains(name))
            return Text($"'{name}' is not available on this server.", isError: true);

        var sessions = services.GetRequiredService<ISessionManager>();

        switch (name)
        {
            case "list_agents":
            {
                var agents = await services.GetRequiredService<IEntityStore<Agent>>().GetAllAsync(ct);
                return Text(string.Join("\n", agents.Select(a =>
                    $"{a.Id}  {a.Name}  provider={a.Provider}  permissions={a.PermissionMode}  enabled={a.Enabled}")));
            }

            case "list_sessions":
            {
                var all = await sessions.GetAllAsync(ct);
                var status = arguments["status"]?.GetValue<string>();
                var projectId = arguments["projectId"]?.GetValue<string>();
                var limit = (int?)arguments["limit"]?.GetValue<double>() ?? 25;

                var filtered = all
                    .Where(s => !s.IsArchived)
                    .Where(s => status is null || string.Equals(s.Status.ToString(), status, StringComparison.OrdinalIgnoreCase))
                    .Where(s => projectId is null || s.ProjectId == projectId)
                    .Take(Math.Clamp(limit, 1, 200));

                return Text(string.Join("\n", filtered.Select(s =>
                    $"{s.Id}  [{s.Status}]  {s.AgentName} ({s.Provider})  {s.Title}  dir={s.WorkingDirectory}")));
            }

            case "get_session":
            {
                var session = await sessions.GetAsync(Required(arguments, "sessionId"), ct);
                if (session is null)
                    return Text("No session with that id.", isError: true);

                var transcript = string.Join("\n\n", session.Messages.Select(m => $"[{m.Role}] {m.Content}"));
                return Text($"{session.Title} — {session.Status} — {session.WorkingDirectory}\n\n{transcript}");
            }

            case "send_message":
            {
                await sessions.SendAsync(Required(arguments, "sessionId"), Required(arguments, "message"), ct);
                return Text("Queued.");
            }

            case "start_session":
            {
                var session = await sessions.StartAsync(new StartSessionRequest
                {
                    AgentId = Required(arguments, "agentId"),
                    Prompt = Required(arguments, "prompt"),
                    ProjectId = arguments["projectId"]?.GetValue<string>(),
                }, ct);

                return Text($"Started session {session.Id} in {session.WorkingDirectory}.");
            }

            case "send_guidance":
            {
                var interrupt = arguments["interrupt"]?.GetValue<bool>() ?? false;
                var agentName = parameters?["_meta"]?["agentName"]?.GetValue<string>();

                var message = await sessions.SendGuidanceAsync(
                    Required(arguments, "sessionId"),
                    Required(arguments, "guidance"),
                    agentName is null ? "agent" : $"agent:{agentName}",
                    interrupt,
                    ct);

                return Text(interrupt
                    ? "Guidance sent and the turn in flight was stopped so it applies now."
                    : $"Guidance sent (id {message.Id}). The agent picks it up on its next check, or at the start of its next turn.");
            }

            case "check_guidance":
            {
                var pending = await sessions.TakeGuidanceAsync(Required(arguments, "sessionId"), ct);
                if (pending.Count == 0)
                    return Text("No new guidance. Carry on.");

                var lines = string.Join(Environment.NewLine, pending.Select(g => $"- [{g.Source}] {g.Text}"));
                return Text($"""
                             New guidance — this overrides earlier instructions where they conflict:
                             {lines}
                             """);
            }

            case "get_notes":
            {
                var session = await sessions.GetAsync(Required(arguments, "sessionId"), ct);
                return Text(string.IsNullOrWhiteSpace(session?.Notes) ? "(no notes)" : session!.Notes);
            }

            case "add_note":
            {
                var store = services.GetRequiredService<IEntityStore<ChatSession>>();
                var sessionId = Required(arguments, "sessionId");
                var session = await store.GetAsync(sessionId, ct);
                if (session is null)
                    return Text("No session with that id.", isError: true);

                var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm");
                session.Notes = string.IsNullOrWhiteSpace(session.Notes)
                    ? $"[{stamp}] {Required(arguments, "note")}"
                    : $"{session.Notes}\n[{stamp}] {Required(arguments, "note")}";

                await store.UpsertAsync(session, ct);
                return Text("Note added.");
            }

            case "list_projects":
            {
                var projects = await services.GetRequiredService<IEntityStore<Project>>().GetAllAsync(ct);
                return Text(string.Join("\n\n", projects.Select(p =>
                    $"{p.Id}  {p.Name}\n  files: {string.Join(", ", p.Files.Select(f => f.FileName))}\n  instructions: {p.Instructions}")));
            }

            case "list_workflows":
            {
                var workflows = await services.GetRequiredService<IEntityStore<Workflow>>().GetAllAsync(ct);
                return Text(string.Join("\n", workflows.Select(w =>
                    $"{w.Id}  {w.Name}  inputs=[{string.Join(", ", w.Inputs.Select(i => i.Name))}]  steps={w.Steps.Count}")));
            }

            case "run_workflow":
            {
                var inputs = new Dictionary<string, string>();
                if (arguments["inputs"] is JsonObject supplied)
                {
                    foreach (var pair in supplied)
                        inputs[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                }

                var run = await services.GetRequiredService<IWorkflowEngine>()
                    .StartAsync(Required(arguments, "workflowId"), inputs, "mcp", ct);

                return Text($"Started workflow run {run.Id}.");
            }

            case "list_queues":
            {
                var queues = services.GetRequiredService<IQueueService>();
                var all = await queues.GetQueuesAsync(ct);
                if (all.Count == 0)
                    return Text("No queues are configured.");

                var lines = new List<string>();
                foreach (var queue in all)
                {
                    var counts = await queues.GetCountsAsync(queue.Id, ct);
                    lines.Add(
                        $"{queue.Id}  {queue.Name}  target={queue.Target}:{queue.TargetId}  " +
                        $"enabled={queue.Enabled}  dedupe={queue.Dedupe}  " +
                        $"pending={counts.Pending} running={counts.Running} failed={counts.Failed}");
                }

                return Text(string.Join("\n", lines));
            }

            case "enqueue":
            {
                var queues = services.GetRequiredService<IQueueService>();
                var target = await queues.ResolveQueueAsync(Required(arguments, "queueId"), ct);
                if (target is null)
                    return Text(await UnknownQueueAsync(queues, arguments["queueId"]?.GetValue<string>(), ct), isError: true);

                var payload = new Dictionary<string, string>();
                if (arguments["payload"] is JsonObject supplied)
                {
                    foreach (var pair in supplied)
                        payload[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                }

                var agentName = parameters?["_meta"]?["agentName"]?.GetValue<string>();

                var result = await queues.EnqueueAsync(new EnqueueRequest
                {
                    QueueId = target.Id,
                    Key = arguments["key"]?.GetValue<string>() ?? string.Empty,
                    Title = arguments["title"]?.GetValue<string>() ?? string.Empty,
                    Body = arguments["body"]?.GetValue<string>() ?? string.Empty,
                    Payload = payload,
                    Priority = (int?)arguments["priority"]?.GetValue<double>() ?? 0,
                    EnqueuedBy = agentName is null ? "mcp" : $"agent:{agentName}",
                }, ct);

                // A duplicate is not an error: a poller re-reporting work already queued is the
                // expected case, and reporting it as a failure would have the agent retrying.
                return Text(result.Accepted
                    ? $"Queued as {result.Item!.Id} on '{target.Name}'."
                    : $"Not queued on '{target.Name}'. {result.Reason}");
            }

            case "list_queue_items":
            {
                var queues = services.GetRequiredService<IQueueService>();
                var queue = await queues.ResolveQueueAsync(Required(arguments, "queueId"), ct);
                if (queue is null)
                    return Text(await UnknownQueueAsync(queues, arguments["queueId"]?.GetValue<string>(), ct), isError: true);

                var status = arguments["status"]?.GetValue<string>();
                var limit = (int?)arguments["limit"]?.GetValue<double>() ?? 25;

                var items = (await queues.GetItemsAsync(queue.Id, ct))
                    .Where(i => status is null || string.Equals(i.Status.ToString(), status, StringComparison.OrdinalIgnoreCase))
                    .Take(Math.Clamp(limit, 1, 200));

                return Text(string.Join("\n", items.Select(i =>
                    $"{i.Id}  [{i.Status}]  attempts={i.Attempts}  key={i.Key}  {i.Title}" +
                    (i.LastError is { Length: > 0 } error ? $"  error={error}" : string.Empty))));
            }

            // The CLI-driven providers bring their own toolsets and never see the orchestrator's
            // internal ones, so this is how Claude and Codex sessions reach image generation.
            case "generate_image":
            {
                var images = services.GetRequiredService<IImageGenerationService>();

                if (!images.AnyConfigured)
                    return Text("No image backend is configured.", isError: true);

                ImageBackend? backend = Enum.TryParse<ImageBackend>(
                    arguments["backend"]?.GetValue<string>(), true, out var parsed) ? parsed : null;

                var image = await images.GenerateAsync(
                    new ImageRequest
                    {
                        Prompt = Required(arguments, "prompt"),
                        Width = (int?)arguments["width"]?.GetValue<double>() ?? 1024,
                        Height = (int?)arguments["height"]?.GetValue<double>() ?? 1024,
                        Seed = (int?)arguments["seed"]?.GetValue<double>(),
                    },
                    backend,
                    arguments["sessionId"]?.GetValue<string>(),
                    ct);

                // Handed back as markdown so a CLI that quotes it verbatim gets an inline picture.
                // The transcript also renders a bare /images/ path, so a paraphrase still shows it.
                return Text(
                    $"Generated a {image.Width}×{image.Height} image with {image.Backend} ({image.Model}). " +
                    $"Include this line in your reply exactly as written so the user can see it:\n\n" +
                    $"![{image.Prompt}]({images.UrlFor(image)})");
            }

            default:
                return Text($"Unknown tool '{name}'.", isError: true);
        }
    }

    /// <summary>
    /// The refusal an agent gets when it names a queue that is not there. It lists the queues so the
    /// next call can be right, rather than leaving the agent to ask the operator for the name again.
    /// </summary>
    private static async Task<string> UnknownQueueAsync(IQueueService queues, string? wanted, CancellationToken ct)
    {
        var all = await queues.GetQueuesAsync(ct);

        return all.Count == 0
            ? $"There is no queue '{wanted}'. No queues are configured."
            : $"There is no queue '{wanted}'. The queues are: " +
              string.Join(", ", all.Select(q => $"{q.Name} (id {q.Id})")) + ".";
    }

    private static string Required(JsonObject arguments, string key) =>
        arguments[key]?.GetValue<string>()
        ?? throw new InvalidOperationException($"'{key}' is required.");

    private static JsonObject Text(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Length > 0)
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static JsonObject Props(params (string Name, string Type, string Description)[] properties)
    {
        var result = new JsonObject();
        foreach (var (name, type, description) in properties)
            result[name] = new JsonObject { ["type"] = type, ["description"] = description };

        return result;
    }

    private static JsonObject Success(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
