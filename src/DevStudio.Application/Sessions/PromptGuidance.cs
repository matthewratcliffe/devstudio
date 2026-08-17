namespace DevStudio.Application.Sessions;

/// <summary>
/// A small, deterministic prompt intake flow. It deliberately asks at most one useful question at
/// a time and never calls a model: the answers are folded into a compact prompt before the CLI runs.
/// </summary>
public sealed class PromptGuidance
{
    private readonly List<string> _answers = [];

    public PromptGuidance(string originalPrompt) => OriginalPrompt = originalPrompt.Trim();

    public string OriginalPrompt { get; }

    public IReadOnlyList<string> Answers => _answers;

    public string? NextQuestion
    {
        get
        {
            var text = string.Join("\n", new[] { OriginalPrompt }.Concat(_answers));

            if (IsVagueGoal(OriginalPrompt))
                return "What should the assistant accomplish? Include the specific outcome you want.";

            if (!HasContext(text))
                return "What context matters here—who or what is this for, and which files, system, or situation should it consider?";

            if (!HasConstraintsOrOutput(text))
                return "Are there constraints or success criteria, and what should the final answer look like?";

            return null;
        }
    }

    public void AddAnswer(string answer)
    {
        if (!string.IsNullOrWhiteSpace(answer))
            _answers.Add(answer.Trim());
    }

    public string BuildPrompt() =>
        $"Goal:\n{OriginalPrompt}\n\n" +
        (_answers.Count == 0 ? string.Empty : $"Additional context and requirements:\n- {string.Join("\n- ", _answers)}\n\n") +
        "Instructions: Use the information above, state assumptions when necessary, and provide a focused answer.";

    private static bool IsVagueGoal(string prompt)
    {
        var text = prompt.Trim();
        return text.Length < 20 || text.Equals("help", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("fix it", StringComparison.OrdinalIgnoreCase) ||
               text.Equals("do this", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasContext(string text) =>
        text.Length >= 90 || ContainsAny(text, "for ", "in ", "using ", "with ", "project", "file", "code", "customer", "team", "because", "background");

    private static bool HasConstraintsOrOutput(string text) =>
        ContainsAny(text, "must", "should", "don't", "do not", "avoid", "constraint", "success", "acceptance", "format", "return", "list", "summary", "checklist", "explain", "write");

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
