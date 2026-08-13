using DevStudio.Domain.Agents;
using DevStudio.Domain.Sessions;

namespace DevStudio.Application.Agents;

/// <summary>What a turn runs on, and whether that is still the opening model.</summary>
public readonly record struct ModelChoice(string? Model, string? Effort, bool IsOpening);

/// <summary>
/// Which model a turn should use. A session can open on one model and hand over to another after a
/// set number of turns — a strong model to work out what to do, a cheaper one to carry it out —
/// which is configured on the agent and overridable per conversation from the chat itself.
/// </summary>
public static class ModelSchedule
{
    /// <summary>
    /// The choice for the next turn of <paramref name="session"/>. Its own settings win over the
    /// agent's, and <see cref="ChatSession.TurnCount"/> is the number of turns already finished, so
    /// an opening of two covers the first and second turns.
    /// </summary>
    public static ModelChoice For(Agent agent, ChatSession session) =>
        For(agent, session, session.TurnCount);

    public static ModelChoice For(Agent agent, ChatSession session, int completedTurns)
    {
        var model = Blank(session.Model) ?? Blank(agent.Model);
        var effort = Blank(session.Effort) ?? Blank(agent.Effort);

        var openingTurns = session.OpeningTurns ?? agent.OpeningTurns;
        var openingModel = Blank(session.OpeningModel) ?? Blank(agent.OpeningModel);
        var openingEffort = Blank(session.OpeningEffort) ?? Blank(agent.OpeningEffort);

        // A handover needs a number of turns and something to differ in. Naming neither is the
        // ordinary case: one model for the whole conversation.
        if (openingTurns <= 0 || completedTurns >= openingTurns || (openingModel is null && openingEffort is null))
            return new ModelChoice(model, effort, false);

        // Either half of the opening can be left blank to change only the other — "the same model,
        // thinking harder to begin with" is as reasonable as swapping the model outright.
        return new ModelChoice(openingModel ?? model, openingEffort ?? effort, true);
    }

    /// <summary>
    /// Whether a handover is configured at all, for the UI to say so without repeating the rules.
    /// </summary>
    public static bool HasHandover(Agent agent, ChatSession session)
    {
        var turns = session.OpeningTurns ?? agent.OpeningTurns;
        var model = Blank(session.OpeningModel) ?? Blank(agent.OpeningModel);
        var effort = Blank(session.OpeningEffort) ?? Blank(agent.OpeningEffort);

        return turns > 0 && (model is not null || effort is not null);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Model settings chosen for one conversation rather than for the agent behind it. Every field is
/// optional: null means "whatever the agent says", which is what an untouched chat sends.
/// </summary>
public sealed record SessionModelSettings(
    string? Model = null,
    string? Effort = null,
    string? OpeningModel = null,
    string? OpeningEffort = null,
    int? OpeningTurns = null)
{
    public void ApplyTo(ChatSession session)
    {
        session.Model = Model;
        session.Effort = Effort;
        session.OpeningModel = OpeningModel;
        session.OpeningEffort = OpeningEffort;
        session.OpeningTurns = OpeningTurns;
    }
}
