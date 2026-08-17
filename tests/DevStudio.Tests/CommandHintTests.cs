using DevStudio.Application.Sessions;
using DevStudio.Domain.Providers;
using DevStudio.Domain.Sessions;

namespace DevStudio.Tests;

public class CommandHintTests
{
    private static ChatSession Session(string text, AiProvider provider = AiProvider.Claude) => new()
    {
        Provider = provider,
        Status = SessionStatus.AwaitingInput,
        Messages = [new ChatMessage { Role = MessageRole.Agent, Content = text }],
    };

    [Fact]
    public void A_failing_problem_suggests_debug_for_claude()
    {
        var hint = CommandHints.For(Session("The tests fail with a stack trace."));

        Assert.NotNull(hint);
        Assert.Equal("/debug", hint.Command);
        Assert.Contains("code.claude.com", hint.DocumentationUrl);
    }

    [Fact]
    public void A_failing_problem_maps_to_codex_diagnostics()
    {
        var hint = CommandHints.For(Session("The build is broken and reports an exception.", AiProvider.Codex));

        Assert.NotNull(hint);
        Assert.Equal("/debug-config", hint.Command);
        Assert.Contains("developers.openai.com/codex", hint.DocumentationUrl);
    }

    [Fact]
    public void A_review_request_uses_the_provider_equivalent()
    {
        Assert.Equal("/code-review", CommandHints.For(Session("Please review the current diff."))!.Command);
        Assert.Equal("/review", CommandHints.For(Session("Please review the current diff.", AiProvider.Codex))!.Command);
    }

    [Fact]
    public void No_hint_is_shown_while_the_agent_is_running()
    {
        var session = Session("The tests fail.");
        session.Status = SessionStatus.Running;

        Assert.Null(CommandHints.For(session));
    }

    [Fact]
    public void Unrelated_chat_does_not_get_a_command_nudge()
    {
        Assert.Null(CommandHints.For(Session("Here is the requested explanation.")));
    }
}
