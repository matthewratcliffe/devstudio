using DevStudio.Application.Agents;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Sessions;

namespace DevStudio.Tests;

public class TokenMinimisationTests
{
    [Fact]
    public void Nothing_selected_composes_nothing()
    {
        Assert.Equal(string.Empty, TokenMinimisation.Compose(TokenTactics.None));
    }

    [Fact]
    public void Only_the_selected_tactics_are_instructed()
    {
        var prompt = TokenMinimisation.Compose(TokenTactics.TerseReplies | TokenTactics.ScopedTests);

        Assert.Contains("## Token minimisation", prompt);
        Assert.Contains("Terse replies", prompt);
        Assert.Contains("Narrow tests", prompt);
        Assert.DoesNotContain("Delegate searching", prompt);
    }

    /// <summary>
    /// The saving comes out of narration and rework, never out of the work. Without that the block
    /// reads as permission to answer from a guess rather than from a file.
    /// </summary>
    [Fact]
    public void The_block_says_it_does_not_licence_cutting_corners()
    {
        var prompt = TokenMinimisation.Compose(TokenTactics.TerseReplies);

        Assert.Contains("Do not skip a step", prompt);
    }

    [Fact]
    public void A_session_follows_its_agent_until_it_chooses_for_itself()
    {
        var agent = new Agent { TokenMinimisation = TokenTactics.NarrowReads | TokenTactics.StayInScope };
        var session = new ChatSession();

        Assert.Equal(agent.TokenMinimisation, TokenMinimisation.For(agent, session));

        session.TokenMinimisation = TokenTactics.TerseReplies;
        Assert.Equal(TokenTactics.TerseReplies, TokenMinimisation.For(agent, session));

        // Switching everything off in a chat is a choice of its own, not a return to the agent's.
        session.TokenMinimisation = TokenTactics.None;
        Assert.Equal(TokenTactics.None, TokenMinimisation.For(agent, session));

        session.TokenMinimisation = null;
        Assert.Equal(agent.TokenMinimisation, TokenMinimisation.For(agent, session));
    }

    [Fact]
    public void Tactics_go_on_and_off_one_at_a_time()
    {
        var tactics = TokenMinimisation.With(TokenTactics.None, TokenTactics.BatchTools, on: true);
        tactics = TokenMinimisation.With(tactics, TokenTactics.PlanFirst, on: true);

        Assert.True(TokenMinimisation.Has(tactics, TokenTactics.BatchTools));
        Assert.True(TokenMinimisation.Has(tactics, TokenTactics.PlanFirst));

        tactics = TokenMinimisation.With(tactics, TokenTactics.BatchTools, on: false);

        Assert.False(TokenMinimisation.Has(tactics, TokenTactics.BatchTools));
        Assert.True(TokenMinimisation.Has(tactics, TokenTactics.PlanFirst));
    }

    /// <summary>Every flag is offered in the UI, or one could never be switched off again.</summary>
    [Fact]
    public void Every_tactic_is_in_the_catalogue_exactly_once()
    {
        var flags = Enum.GetValues<TokenTactics>().Where(t => t != TokenTactics.None).ToList();

        Assert.Equal(flags.Count, TokenMinimisation.All.Count);
        Assert.Equal(TokenMinimisation.All.Count, TokenMinimisation.All.Select(t => t.Flag).Distinct().Count());
        Assert.All(TokenMinimisation.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Instruction)));
    }
}
