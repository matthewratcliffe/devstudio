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
    /// agent's, and <see cref="ChatSession.TurnCount"/> counts the messages already exchanged — one
    /// for what you send and one for what comes back — so an opening of two covers the first
    /// exchange, and the turn about to run is chosen on what happened before it.
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
        // ordinary case: one model for the whole conversation. The agent asking for the change
        // itself ends the opening wherever the count had got to.
        if (session.HandoverRequested
            || openingTurns <= 0
            || completedTurns >= openingTurns
            || (openingModel is null && openingEffort is null))
            return new ModelChoice(model, effort, false);

        // Either half of the opening can be left blank to change only the other — "the same model,
        // thinking harder to begin with" is as reasonable as swapping the model outright.
        return new ModelChoice(openingModel ?? model, openingEffort ?? effort, true);
    }

    /// <summary>What the conversation settles on once its opening is over, however that happens.</summary>
    public static ModelChoice Settled(Agent agent, ChatSession session) =>
        For(agent, session, int.MaxValue);

    /// <summary>
    /// The next model down <paramref name="models"/> from <paramref name="current"/> — the lists are
    /// written strongest first, so the one after it is the cheaper one. Null when the current model
    /// is unknown, is not in the list, or is already the last of them: guessing at a model nobody
    /// configured would be worse than leaving the conversation where it is.
    /// </summary>
    public static string? NextDown(string? current, IReadOnlyList<string> models)
    {
        if (Blank(current) is not { } model)
            return null;

        var index = models.ToList().FindIndex(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));

        return index >= 0 && index + 1 < models.Count ? models[index + 1] : null;
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

    /// <summary>
    /// A model and the level it thinks at as one phrase — "sonnet (high)" — so the transcript, the
    /// session panel and the handover notice all name a model the same way.
    /// </summary>
    public static string Describe(string? model, string? effort)
    {
        var name = Blank(model) ?? "CLI default";
        var level = Blank(effort);

        return level is null ? name : $"{name} ({level})";
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
