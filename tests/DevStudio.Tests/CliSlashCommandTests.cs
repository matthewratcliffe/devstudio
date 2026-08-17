using DevStudio.Application.Sessions;
using DevStudio.Domain.Providers;

namespace DevStudio.Tests;

public class CliSlashCommandTests
{
    [Fact]
    public void Claude_completion_uses_only_claude_commands()
    {
        var commands = CliSlashCommands.For(AiProvider.Claude, "/re");

        Assert.Contains(commands, command => command.Name == "/resume");
        Assert.DoesNotContain(commands, command => command.Name == "/review");
    }

    [Fact]
    public void Codex_completion_uses_codex_commands()
    {
        var commands = CliSlashCommands.For(AiProvider.Codex, "/re");

        Assert.Contains(commands, command => command.Name == "/reset");
        Assert.Contains(commands, command => command.Name == "/resume");
    }

    [Fact]
    public void A_command_with_arguments_still_completes_from_its_name()
    {
        var commands = CliSlashCommands.For(AiProvider.Codex, "/model gpt-5");

        Assert.Single(commands);
        Assert.Equal("/model", commands[0].Name);
    }

    [Fact]
    public void Completion_includes_description_and_usage()
    {
        var command = Assert.Single(CliSlashCommands.For(AiProvider.Codex, "/use"));

        Assert.Equal("load a skill", command.Description);
        Assert.Equal("/use <skill>", command.Usage);
    }

    [Fact]
    public void Inline_slashes_are_not_treated_as_commands()
    {
        Assert.Empty(CliSlashCommands.For(AiProvider.Claude, "explain /model"));
        Assert.NotEmpty(CliSlashCommands.For(AiProvider.Claude, "/"));
    }
}
