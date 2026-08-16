using System.Text;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Sessions;

namespace DevStudio.Application.Agents;

/// <summary>One tactic: what it is called in the UI, and what the agent is actually told.</summary>
/// <param name="Flag">The bit this entry describes.</param>
/// <param name="Label">Short name for a checkbox.</param>
/// <param name="Hint">One line under the checkbox, for someone deciding whether to switch it on.</param>
/// <param name="Instruction">The sentences composed into the system prompt when it is on.</param>
public sealed record TokenTactic(TokenTactics Flag, string Label, string Hint, string Instruction);

/// <summary>
/// The token minimisation section: which cost-saving tactics are in force for a turn, and the
/// instruction block that puts them to the agent.
///
/// An agent carries a default selection and a conversation can override it, the same way a model is
/// chosen. The prompt is composed fresh for every turn, so a tactic switched on or off mid-chat
/// takes effect from the next message with nothing to restart.
/// </summary>
public static class TokenMinimisation
{
    /// <summary>
    /// Safe defaults for newly created agents and quick chats. They reduce repetition and
    /// exploration without weakening verification or moving work to another model.
    /// </summary>
    public const TokenTactics RecommendedDefaults = TokenTacticsDefaults.Recommended;

    /// <summary>
    /// Every tactic in the order it is offered, roughly from what it changes about answering to what
    /// it changes about the work itself.
    /// </summary>
    public static readonly IReadOnlyList<TokenTactic> All =
    [
        new(TokenTactics.TerseReplies,
            "Terse replies",
            "Answer and stop — no preamble, no recap.",
            "Answer in as few words as the answer needs. No preamble, no restating the request, no "
            + "narrating what you are about to do, no summary of what the reader has just seen, no "
            + "closing offer of further help. Do not repeat file contents or a diff back when the "
            + "tool output already showed them."),

        new(TokenTactics.NarrowReads,
            "Read narrowly",
            "Search first, then read the lines that matter.",
            "Search before you read. Find the relevant lines with grep or a glob and read only those, "
            + "with an offset and a limit; read a whole file only when you already know it is small. "
            + "Never re-read something that is already in this conversation."),

        new(TokenTactics.DelegateSearch,
            "Delegate searching",
            "Send broad exploration to a subagent, keep the conclusion.",
            "Hand broad or open-ended searching to a subagent and keep only its conclusion. Sweeping "
            + "many files costs far more in this conversation than the answer is worth carrying. "
            + "Search directly only when you already know the file or symbol."),

        new(TokenTactics.BatchTools,
            "Batch tool calls",
            "Independent calls go out together, not one per turn.",
            "Issue independent tool calls together in one message rather than one per turn. Every "
            + "round trip re-sends the whole conversation, so the number of turns costs more than "
            + "the number of calls."),

        new(TokenTactics.TrustResults,
            "Trust tool results",
            "No reading a file back to confirm an edit that worked.",
            "Believe a tool that reported success. Do not re-read a file to confirm an edit landed, "
            + "and do not re-run a command to see the same output a second time."),

        new(TokenTactics.QuietCommands,
            "Quiet command output",
            "Filter output at the source instead of reading all of it.",
            "Filter command output where it is produced. Pipe through head, tail or grep, prefer a "
            + "quiet or summary flag, and ask for the failing lines rather than the whole log. Never "
            + "print a large file when a targeted search answers the question."),

        new(TokenTactics.ScopedTests,
            "Narrow tests",
            "Run the nearest test that proves the change.",
            "Run the narrowest check that proves the change — the single test, file or project it "
            + "touches. Leave the full suite to CI or to the end, once the narrow one passes."),

        new(TokenTactics.PlanFirst,
            "Plan before editing",
            "Rework costs more than thinking it through.",
            "Settle the approach before touching files. Code written, read back and thrown away costs "
            + "several times what working it out first does. Where two readings of the task would "
            + "mean materially different work, ask rather than building both."),

        new(TokenTactics.StayInScope,
            "Stay in scope",
            "What was asked, and nothing beside it.",
            "Build what was asked and stop. No unrequested refactors, tests, documentation, comments "
            + "or defensive extras, and no wandering into code the task does not touch. Mention "
            + "anything else worth doing instead of doing it."),

        new(TokenTactics.SummariseEarly,
            "Summarise early",
            "Reduce long output to its finding while the context is small.",
            "Keep the working context small. Reduce long tool output to the finding as soon as you "
            + "have it, carry that forward instead of the raw text, and let the conversation be "
            + "summarised rather than dragging its whole history along."),

        new(TokenTactics.SurgicalEdits,
            "Edit, don't rewrite",
            "Rewriting a file costs its whole length, every time.",
            "Change files in place with targeted edits. Rewriting a file whole costs its entire "
            + "length in output tokens for the sake of a few lines, and buries what actually "
            + "changed. Reach for a full rewrite only when most of the file is genuinely changing."),

        new(TokenTactics.RecommendDontSurvey,
            "Recommend, don't survey",
            "One recommendation beats four options you will not take.",
            "When there is a choice to make, give the recommendation and the one reason for it. Do "
            + "not lay out every option you considered and discarded, and do not write out an "
            + "approach you are not going to take."),

        new(TokenTactics.FailFast,
            "Fail fast",
            "Two goes at the same thing, then report.",
            "Stop after two failed attempts at the same problem and report what you tried and what "
            + "it did. A loop of near-identical retries is the most expensive thing that can happen "
            + "in a conversation, and it almost never ends in the fix."),

        new(TokenTactics.HandOverWhenMechanical,
            "Hand over when mechanical",
            "Ask for the cheaper model once the thinking is done.",
            "When the remaining work is mechanical — carrying out a plan already settled — ask to "
            + "move to the cheaper model by writing [CHANGE MODEL] on a line of its own. Only do it "
            + "once the decisions are made: the model that takes over will carry them out, not "
            + "revisit them."),

        new(TokenTactics.ReuseContext,
            "Reuse established context",
            "Carry forward facts already established instead of asking again.",
            "Treat facts, paths and results already established in this conversation as available "
            + "context. Do not ask the user or call a tool to rediscover them unless they may have "
            + "changed or the existing evidence is insufficient."),

        new(TokenTactics.AvoidSpeculativeWork,
            "Avoid speculative work",
            "Follow evidence and the request, not hypothetical branches.",
            "Do not investigate hypothetical bugs, edge cases, refactors or improvements without "
            + "evidence they matter or a request to handle them. Finish the evidenced path first "
            + "and mention adjacent risks briefly instead of exploring them."),

        new(TokenTactics.MinimalToolInputs,
            "Minimise tool inputs",
            "Send only the paths, fields and arguments this call needs.",
            "Keep tool inputs narrow. Pass only the paths, fields, arguments and context needed for "
            + "this call; avoid broad globs, duplicate arguments and embedding information already "
            + "available to the tool."),

        new(TokenTactics.PreferDeltas,
            "Prefer deltas",
            "Ask for changes and summaries instead of unchanged full content.",
            "When context is large, prefer a diff, status, summary or incremental range over the "
            + "unchanged full file, transcript or log. Carry forward the unchanged baseline and "
            + "request only what has changed."),
    ];

    /// <summary>
    /// What is in force for <paramref name="session"/>. The conversation's own selection wins when it
    /// has one; null — which is what every chat started before anyone touched this holds — leaves the
    /// agent's default in charge.
    /// </summary>
    public static TokenTactics For(Agent agent, ChatSession session) =>
        session.TokenMinimisation ?? agent.TokenMinimisation;

    /// <summary>Whether <paramref name="tactic"/> is switched on in <paramref name="tactics"/>.</summary>
    public static bool Has(TokenTactics tactics, TokenTactics tactic) => (tactics & tactic) == tactic;

    public static TokenTactics With(TokenTactics tactics, TokenTactics tactic, bool on) =>
        on ? tactics | tactic : tactics & ~tactic;

    /// <summary>The tactics switched on, in the order they are offered.</summary>
    public static IEnumerable<TokenTactic> Selected(TokenTactics tactics) =>
        All.Where(t => Has(tactics, t.Flag));

    /// <summary>
    /// The instruction block for a turn, or an empty string when nothing is switched on. The
    /// preamble is what keeps this from being read as licence to cut corners: these tactics buy
    /// their saving out of narration and re-reading, never out of the work itself.
    /// </summary>
    public static string Compose(TokenTactics tactics)
    {
        var selected = Selected(tactics).ToList();
        if (selected.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("## Token minimisation");
        builder.AppendLine(
            "The rules below are in force for this conversation and govern how you go about the "
            + "work. They pay for themselves out of narration, re-reading and rework — never out of "
            + "the work itself. Do not skip a step, drop a requirement, or guess at something you "
            + "could have read, to save tokens. If following one of them would make the answer wrong "
            + "or incomplete, set it aside for that step and say in one line that you did.");
        builder.AppendLine();

        foreach (var tactic in selected)
            builder.AppendLine($"- **{tactic.Label}.** {tactic.Instruction}");

        return builder.ToString();
    }

    /// <summary>"3 of 10 on", for a panel with no room to list them.</summary>
    public static string Describe(TokenTactics tactics)
    {
        var count = Selected(tactics).Count();
        return count == 0 ? "off" : $"{count} of {All.Count} on";
    }
}
