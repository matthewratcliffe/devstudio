namespace DevStudio.Application.Sessions;

/// <summary>Small, deterministic prompt-writing nudges shown beside the chat composer.</summary>
public static class PromptingTips
{
    public static IReadOnlyList<string> For(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var tips = new List<string>();
        var text = prompt.Trim();

        if (text.Length < 35)
            tips.Add("State the goal and the context: what should change, and where should the agent look?");

        if (text.Length > 500)
            tips.Add("Split long prompts into goal, constraints, and expected output so the agent can focus on what matters.");

        if (!text.Contains("because", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("must", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("should", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("don't", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("do not", StringComparison.OrdinalIgnoreCase))
            tips.Add("Add constraints or acceptance criteria; clear boundaries reduce rework and unnecessary tool calls.");

        if (!text.Contains("format", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("return", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("list", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("summar", StringComparison.OrdinalIgnoreCase))
            tips.Add("Say what a useful answer looks like (for example: a short summary, a patch, or a checklist).");

        if (text.Length > 180 && tips.Count < 3)
            tips.Add("To lower cost, name the smallest files, time range, or scope needed instead of asking for a broad scan.");

        return tips.Take(3).ToList();
    }
}
