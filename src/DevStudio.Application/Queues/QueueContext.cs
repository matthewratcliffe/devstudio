using DevStudio.Application.Common;
using DevStudio.Domain.Queues;

namespace DevStudio.Application.Queues;

/// <summary>
/// Turns an item into the values that reach the thing processing it — a rendered prompt for an
/// agent, a set of inputs for a workflow. Kept out of the dispatcher so both targets read from one
/// definition of what an item exposes.
/// </summary>
public static class QueueContext
{
    /// <summary>
    /// Everything an item publishes, keyed as templates reference it. Payload entries appear bare
    /// and under a payload. prefix; the queue's own inputs sit underneath so an item can override
    /// them.
    /// </summary>
    public static Dictionary<string, string> Build(WorkQueue queue, QueueItem item)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in queue.Inputs)
            values[pair.Key] = pair.Value;

        foreach (var pair in item.Payload)
        {
            values[pair.Key] = pair.Value;
            values[$"payload.{pair.Key}"] = pair.Value;
        }

        values["item.id"] = item.Id;
        values["item.key"] = item.Key;
        values["item.title"] = item.Title;
        values["item.body"] = item.Body;
        values["item.priority"] = item.Priority.ToString();
        values["item.attempts"] = item.Attempts.ToString();
        values["queue.name"] = queue.Name;
        values["queue.id"] = queue.Id;

        return values;
    }

    /// <summary>
    /// The prompt for an agent. An empty template is not an error — it means "just hand the agent
    /// the item", which is the whole instruction for a queue whose agent already knows its job from
    /// its system prompt.
    /// </summary>
    public static string RenderPrompt(WorkQueue queue, QueueItem item)
    {
        var values = Build(queue, item);

        if (!string.IsNullOrWhiteSpace(queue.PromptTemplate))
            return TemplateRenderer.Render(queue.PromptTemplate, values);

        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.Title))
            lines.Add(item.Title);

        if (!string.IsNullOrWhiteSpace(item.Key) && !string.Equals(item.Key, item.Title, StringComparison.Ordinal))
            lines.Add($"Reference: {item.Key}");

        if (!string.IsNullOrWhiteSpace(item.Body))
        {
            lines.Add(string.Empty);
            lines.Add(item.Body);
        }

        foreach (var pair in item.Payload)
            lines.Add($"{pair.Key}: {pair.Value}");

        return string.Join(Environment.NewLine, lines).Trim();
    }

    /// <summary>Inputs for a workflow run: the queue's constants, then the item over the top.</summary>
    public static Dictionary<string, string> BuildInputs(WorkQueue queue, QueueItem item)
    {
        var inputs = Build(queue, item);

        if (!string.IsNullOrWhiteSpace(queue.ProjectId))
            inputs["projectId"] = queue.ProjectId!;

        return inputs;
    }
}
