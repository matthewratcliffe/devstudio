using AiShop.Application.Abstractions;
using AiShop.Application.Common;
using AiShop.Domain.Agents;
using AiShop.Domain.Providers;
using AiShop.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiShop.Tests;

public class CustomCliTests
{
    private static CustomCli Create(CliProvider definition) =>
        new(definition, new UnusedRunner(), new OrchestratorOptions { HomePath = "/home/test" }, NullLogger.Instance);

    private static TurnRequest Turn(string prompt = "do the thing") => new()
    {
        Prompt = prompt,
        WorkingDirectory = "/work",
    };

    [Fact]
    public void A_prompt_with_spaces_stays_one_argument()
    {
        var cli = Create(new CliProvider { Executable = "copilot", PromptArguments = "-p {{prompt}} --allow-all-tools" });

        var arguments = cli.BuildArguments(Turn("refactor the parser and run the tests"));

        Assert.Equal(["-p", "refactor the parser and run the tests", "--allow-all-tools"], arguments);
    }

    [Fact]
    public void Optional_arguments_disappear_when_their_token_is_empty()
    {
        var cli = Create(new CliProvider
        {
            PromptArguments = "-p {{prompt}}",
            ModelArguments = "--model {{model}}",
        });

        // No model on the request, so the whole flag is dropped rather than passed empty.
        Assert.Equal(["-p", "do the thing"], cli.BuildArguments(Turn()));

        var withModel = cli.BuildArguments(Turn() with { Model = "gpt-5" });
        Assert.Equal(["--model", "gpt-5", "-p", "do the thing"], withModel);
    }

    [Fact]
    public void Resume_arguments_are_used_only_when_there_is_a_session_to_resume()
    {
        var cli = Create(new CliProvider
        {
            PromptArguments = "-p {{prompt}}",
            ResumeArguments = "--resume {{sessionId}}",
        });

        Assert.Equal(["-p", "do the thing"], cli.BuildArguments(Turn()));

        var resumed = cli.BuildArguments(Turn() with { ResumeSessionId = "abc123" });
        Assert.Equal(["--resume", "abc123", "-p", "do the thing"], resumed);
    }

    [Fact]
    public void The_system_prompt_is_folded_into_the_prompt_when_the_definition_has_nowhere_for_it()
    {
        var cli = Create(new CliProvider { PromptArguments = "-p {{prompt}}" });

        var arguments = cli.BuildArguments(Turn() with { SystemPrompt = "Be terse." });

        Assert.Equal("-p", arguments[0]);
        Assert.StartsWith("Be terse.", arguments[1]);
        Assert.EndsWith("do the thing", arguments[1]);
    }

    [Fact]
    public void The_system_prompt_stays_separate_when_the_definition_asks_for_it()
    {
        var cli = Create(new CliProvider { PromptArguments = "--system {{systemPrompt}} -p {{prompt}}" });

        var arguments = cli.BuildArguments(Turn() with { SystemPrompt = "Be terse." });

        Assert.Equal(["--system", "Be terse.", "-p", "do the thing"], arguments);
    }

    [Fact]
    public void Permission_mode_arguments_are_applied()
    {
        var cli = Create(new CliProvider
        {
            PromptArguments = "-p {{prompt}}",
            PermissionArguments = { ["Plan"] = "--read-only", ["Unrestricted"] = "--yolo" },
        });

        Assert.Contains("--read-only", cli.BuildArguments(Turn() with { PermissionMode = PermissionMode.Plan }));
        Assert.Contains("--yolo", cli.BuildArguments(Turn() with { PermissionMode = PermissionMode.Unrestricted }));
        Assert.DoesNotContain("--read-only", cli.BuildArguments(Turn() with { PermissionMode = PermissionMode.AcceptEdits }));
    }

    [Fact]
    public void Plain_text_output_is_taken_as_the_answer()
    {
        var cli = Create(new CliProvider { OutputFormat = CliOutputFormat.PlainText });

        var events = cli.Translate("Here is the answer.", isError: false).ToList();

        Assert.Single(events);
        Assert.Equal(AgentEventKind.Text, events[0].Kind);
        Assert.StartsWith("Here is the answer.", events[0].Text);
    }

    [Fact]
    public void Json_output_is_read_from_the_configured_properties()
    {
        var cli = Create(new CliProvider
        {
            OutputFormat = CliOutputFormat.JsonLines,
            TextProperty = "message.content",
            SessionIdProperty = "conversation.id",
            ErrorProperty = "failure",
        });

        var events = cli.Translate("""{"conversation":{"id":"c-1"},"message":{"content":"hello"}}""", isError: false).ToList();

        Assert.Contains(events, e => e.Kind == AgentEventKind.SessionId && e.Text == "c-1");
        Assert.Contains(events, e => e.Kind == AgentEventKind.Text && e.Text == "hello");
    }

    [Fact]
    public void A_json_error_property_becomes_an_error_event()
    {
        var cli = Create(new CliProvider { OutputFormat = CliOutputFormat.JsonLines, ErrorProperty = "failure" });

        var events = cli.Translate("""{"failure":"quota exceeded"}""", isError: false).ToList();

        Assert.Single(events);
        Assert.Equal(AgentEventKind.Error, events[0].Kind);
        Assert.Equal("quota exceeded", events[0].Text);
    }

    [Fact]
    public void A_non_json_line_in_a_json_stream_is_logged_rather_than_shown_as_an_answer()
    {
        var cli = Create(new CliProvider { OutputFormat = CliOutputFormat.JsonLines });

        var events = cli.Translate("Welcome to the CLI v1.2.3", isError: false).ToList();

        Assert.Single(events);
        Assert.Equal(AgentEventKind.Log, events[0].Kind);
    }

    /// <summary>The argument and parsing tests never reach the process layer.</summary>
    private sealed class UnusedRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> StreamAsync(ProcessRequest request, Func<string, bool, CancellationToken, Task> onLine, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
