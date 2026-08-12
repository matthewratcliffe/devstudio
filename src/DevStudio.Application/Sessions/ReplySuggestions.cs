using System.Text.RegularExpressions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;

namespace DevStudio.Application.Sessions;

/// <summary>
/// Ready-made replies for the operator to send with one click, read off the conversation so far.
///
/// These are worked out here rather than asked of a model: a suggestion that costs a whole turn to
/// produce, and arrives seconds after the answer it follows, is worth less than the turn it spent.
/// What the agent just said is enough to know whether it asked a question, offered a choice, or
/// finished something and is waiting to be told what next.
/// </summary>
public static partial class ReplySuggestions
{
    /// <summary>More than this and the row stops reading as suggestions and starts reading as a menu.</summary>
    private const int Most = 4;

    /// <summary>A line like "1. Rewrite the parser" or "2) leave it alone".</summary>
    [GeneratedRegex(@"^\s*(\d)[.)]\s+(.{3,60}?)\s*$", RegexOptions.Multiline)]
    private static partial Regex NumberedOption();

    public static IReadOnlyList<string> For(ChatSession session, SourceControlProvider? forge = null)
    {
        // Nothing to answer yet, and nothing to suggest while the agent is still talking.
        if (session.Status is SessionStatus.Starting or SessionStatus.Running)
            return [];

        var last = session.Messages.LastOrDefault(m => m.Role == MessageRole.Agent && m.Content.Length > 0);
        if (last is null)
            return [];

        var suggestions = new List<string>();

        // A turn that broke is the one thing worth acting on before anything the agent said.
        if (session.Status == SessionStatus.Failed || session.LastError is { Length: > 0 })
        {
            suggestions.Add("Work out what went wrong and try again.");
            suggestions.Add("Explain the failure before doing anything else.");
        }

        if (session.Approvals.Any(a => a.Status == ApprovalStatus.Pending))
            suggestions.Add("Carry on without that tool if you can.");

        foreach (var option in Options(last.Content))
            suggestions.Add($"Go with option {option.Number}: {option.Text}");

        if (AsksAQuestion(last.Content))
        {
            suggestions.Add("Yes, go ahead.");
            suggestions.Add("No — hold off on that for now.");
        }

        // A plan-mode session produces a plan and then stops, so the obvious reply is to act on it.
        if (session.PermissionMode == PermissionMode.Plan)
            suggestions.Add("That plan looks right. What would you need to carry it out?");

        if (session.RepositoryId is { Length: > 0 })
        {
            suggestions.Add("Show me the diff of what you changed.");

            if (Touched(session))
            {
                suggestions.Add("Run the tests and report what fails.");
                suggestions.Add(forge == SourceControlProvider.GitLab
                    ? "Commit this and open a merge request."
                    : "Commit this and open a pull request.");
            }
        }

        suggestions.Add("Carry on.");
        suggestions.Add("Summarise what you did and what is left.");

        return [.. suggestions.Distinct().Take(Most)];
    }

    /// <summary>
    /// Whether the agent put a question to the operator. Only the end of the message counts: an
    /// answer that discusses a question in passing is not waiting on one.
    /// </summary>
    private static bool AsksAQuestion(string content)
    {
        var tail = content.TrimEnd();
        if (tail.EndsWith('?'))
            return true;

        // A question followed by a sign-off still ends the message in substance.
        var lastLines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(2);
        return lastLines.Any(line => line.TrimEnd().EndsWith('?'));
    }

    /// <summary>Numbered choices the agent laid out, in the order it gave them.</summary>
    private static IEnumerable<(string Number, string Text)> Options(string content) =>
        NumberedOption().Matches(content)
            .Select(m => (Number: m.Groups[1].Value, Text: m.Groups[2].Value.Trim().TrimEnd('.', ':')))
            // Two is a choice; a long list is a document, and the operator will not pick from chips.
            .Where(o => o.Text.Length > 0)
            .Take(3)
            .ToList() is { Count: >= 2 } options
            ? options
            : [];

    /// <summary>Whether the agent actually changed anything, as opposed to only reading around.</summary>
    private static bool Touched(ChatSession session) =>
        session.Messages.Any(m => m.Role == MessageRole.Tool &&
                                  (m.Content.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
                                   m.Content.Contains("write", StringComparison.OrdinalIgnoreCase) ||
                                   m.Content.Contains("patch", StringComparison.OrdinalIgnoreCase)));
}
