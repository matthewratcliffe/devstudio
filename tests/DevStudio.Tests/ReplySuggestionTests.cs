using DevStudio.Application.Sessions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;

namespace DevStudio.Tests;

/// <summary>
/// Suggested replies are read off the conversation rather than asked of a model, so what matters
/// is that they follow what the agent actually said.
/// </summary>
public class ReplySuggestionTests
{
    private static ChatSession Session(
        string agentSaid,
        SessionStatus status = SessionStatus.AwaitingInput,
        params ChatMessage[] earlier) => new()
    {
        Status = status,
        Messages =
        [
            new ChatMessage { Role = MessageRole.User, Content = "do the thing" },
            .. earlier,
            new ChatMessage { Role = MessageRole.Agent, Content = agentSaid },
        ],
    };

    [Fact]
    public void A_question_is_answered_yes_or_no()
    {
        var suggestions = ReplySuggestions.For(Session("I can rename it, but that touches the public API. Shall I go ahead?"));

        Assert.Contains("Yes, go ahead.", suggestions);
        Assert.Contains(suggestions, s => s.StartsWith("No", StringComparison.Ordinal));
    }

    [Fact]
    public void A_question_in_passing_is_not_treated_as_one_put_to_the_operator()
    {
        var suggestions = ReplySuggestions.For(Session(
            "You asked whether it was thread safe? It was not. I have fixed it and the tests pass."));

        Assert.DoesNotContain("Yes, go ahead.", suggestions);
    }

    [Fact]
    public void Options_the_agent_laid_out_become_one_reply_each()
    {
        var suggestions = ReplySuggestions.For(Session("""
            There are two ways to do this:

            1. Rewrite the parser properly
            2. Patch the one case that fails

            Which would you prefer?
            """));

        Assert.Contains(suggestions, s => s.StartsWith("Go with option 1:", StringComparison.Ordinal));
        Assert.Contains(suggestions, s => s.StartsWith("Go with option 2:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_numbered_line_is_prose_rather_than_a_choice()
    {
        var suggestions = ReplySuggestions.For(Session("I did one thing:\n\n1. Renamed the field\n\nAll done."));

        Assert.DoesNotContain(suggestions, s => s.StartsWith("Go with option", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_turn_leads_with_the_failure()
    {
        var session = Session("I could not finish.", SessionStatus.Failed);
        session.LastError = "the command exited with code 2";

        var suggestions = ReplySuggestions.For(session);

        Assert.StartsWith("Work out what went wrong", suggestions[0]);
    }

    [Fact]
    public void Nothing_is_suggested_while_the_agent_is_still_talking()
    {
        Assert.Empty(ReplySuggestions.For(Session("Working on it", SessionStatus.Running)));
    }

    [Fact]
    public void Nothing_is_suggested_before_the_agent_has_said_anything()
    {
        var session = new ChatSession
        {
            Status = SessionStatus.AwaitingInput,
            Messages = [new ChatMessage { Role = MessageRole.User, Content = "do the thing" }],
        };

        Assert.Empty(ReplySuggestions.For(session));
    }

    [Fact]
    public void Work_in_a_repository_can_be_reviewed_and_opened_for_review()
    {
        var session = Session(
            "Done.",
            SessionStatus.AwaitingInput,
            new ChatMessage { Role = MessageRole.Tool, Content = "Edit src/Parser.cs" });
        session.RepositoryId = "repo-1";

        var gitlab = ReplySuggestions.For(session, SourceControlProvider.GitLab);
        var github = ReplySuggestions.For(session, SourceControlProvider.GitHub);

        Assert.Contains("Show me the diff of what you changed.", gitlab);
        Assert.Contains("Commit this and open a merge request.", gitlab);
        Assert.Contains("Commit this and open a pull request.", github);
    }

    [Fact]
    public void A_session_that_only_read_is_not_offered_a_commit()
    {
        var session = Session(
            "Here is what the code does.",
            SessionStatus.AwaitingInput,
            new ChatMessage { Role = MessageRole.Tool, Content = "Read src/Parser.cs" });
        session.RepositoryId = "repo-1";

        var suggestions = ReplySuggestions.For(session, SourceControlProvider.GitLab);

        Assert.DoesNotContain(suggestions, s => s.Contains("Commit", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plan_mode_session_is_offered_a_way_out_of_planning()
    {
        var session = Session("Here is the plan.");
        session.PermissionMode = PermissionMode.Plan;

        Assert.Contains(ReplySuggestions.For(session), s => s.Contains("plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void There_is_always_something_to_click_and_never_a_wall_of_it()
    {
        var suggestions = ReplySuggestions.For(Session("All done."));

        Assert.NotEmpty(suggestions);
        Assert.True(suggestions.Count <= 4, $"{suggestions.Count} suggestions is a menu, not a hint");
        Assert.Equal(suggestions.Distinct().Count(), suggestions.Count);
    }
}
