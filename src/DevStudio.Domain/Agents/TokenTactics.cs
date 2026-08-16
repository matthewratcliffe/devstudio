namespace DevStudio.Domain.Agents;

/// <summary>
/// Ways of working that cost fewer tokens, each switchable on its own. They are instructions to the
/// agent rather than anything the orchestrator enforces: the selection is composed into the system
/// prompt, so turning one on or off changes how the next turn is carried out and nothing that has
/// already run.
/// </summary>
[Flags]
public enum TokenTactics
{
    None = 0,

    /// <summary>No preamble, no restating, no summary of what was just shown.</summary>
    TerseReplies = 1 << 0,

    /// <summary>Search first and read the lines that matter, not whole files.</summary>
    NarrowReads = 1 << 1,

    /// <summary>Broad searching goes to a subagent; only its conclusion comes back.</summary>
    DelegateSearch = 1 << 2,

    /// <summary>Independent tool calls go out together, because turns cost more than calls.</summary>
    BatchTools = 1 << 3,

    /// <summary>A tool that reported success is believed rather than checked again.</summary>
    TrustResults = 1 << 4,

    /// <summary>Command output is filtered at the source instead of read in full.</summary>
    QuietCommands = 1 << 5,

    /// <summary>The nearest test proves the change; the full suite waits for CI.</summary>
    ScopedTests = 1 << 6,

    /// <summary>The approach is settled before files are touched, because rework is dear.</summary>
    PlanFirst = 1 << 7,

    /// <summary>What was asked and nothing beside it — no unrequested extras.</summary>
    StayInScope = 1 << 8,

    /// <summary>Long output is reduced to its finding while the context is still small.</summary>
    SummariseEarly = 1 << 9,

    /// <summary>Files are edited in place rather than rewritten whole.</summary>
    SurgicalEdits = 1 << 10,

    /// <summary>A recommendation, not a survey of the options that were not taken.</summary>
    RecommendDontSurvey = 1 << 11,

    /// <summary>Two failed attempts at the same thing is a report, not a third attempt.</summary>
    FailFast = 1 << 12,

    /// <summary>
    /// The agent asks for the cheaper model itself once the thinking is done, with the
    /// <c>[CHANGE MODEL]</c> marker.
    /// </summary>
    HandOverWhenMechanical = 1 << 13,
}
