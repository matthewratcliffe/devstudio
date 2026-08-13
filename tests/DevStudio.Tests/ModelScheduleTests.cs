using DevStudio.Application.Agents;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Sessions;

namespace DevStudio.Tests;

/// <summary>
/// A session can open on one model and hand over to another — a strong model to work out what to do,
/// a cheaper one to carry it out. The handover happens on turn boundaries, so the arithmetic is worth
/// pinning down: an off-by-one either wastes the expensive model or never uses it.
/// </summary>
public class ModelScheduleTests
{
    private static readonly Agent Handover = new()
    {
        Model = "sonnet",
        Effort = "low",
        OpeningModel = "fable",
        OpeningEffort = "high",
        OpeningTurns = 2,
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_opening_model_covers_the_first_turns(int completed)
    {
        var choice = ModelSchedule.For(Handover, new ChatSession(), completed);

        Assert.Equal("fable", choice.Model);
        Assert.Equal("high", choice.Effort);
        Assert.True(choice.IsOpening);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    public void Everything_after_the_opening_runs_on_the_cheaper_model(int completed)
    {
        var choice = ModelSchedule.For(Handover, new ChatSession(), completed);

        Assert.Equal("sonnet", choice.Model);
        Assert.Equal("low", choice.Effort);
        Assert.False(choice.IsOpening);
    }

    [Fact]
    public void Without_a_turn_count_there_is_no_handover_at_all()
    {
        var agent = new Agent { Model = "sonnet", OpeningModel = "fable", OpeningTurns = 0 };

        Assert.Equal("sonnet", ModelSchedule.For(agent, new ChatSession(), 0).Model);
        Assert.False(ModelSchedule.HasHandover(agent, new ChatSession()));
    }

    [Fact]
    public void Naming_only_an_opening_effort_keeps_the_model_and_thinks_harder_first()
    {
        var agent = new Agent { Model = "sonnet", Effort = "low", OpeningEffort = "high", OpeningTurns = 1 };

        var opening = ModelSchedule.For(agent, new ChatSession(), 0);
        var rest = ModelSchedule.For(agent, new ChatSession(), 1);

        Assert.Equal(("sonnet", "high"), (opening.Model, opening.Effort));
        Assert.Equal(("sonnet", "low"), (rest.Model, rest.Effort));
    }

    [Fact]
    public void What_the_chat_chose_wins_over_the_agent()
    {
        var session = new ChatSession { Model = "haiku", Effort = "medium" };

        var choice = ModelSchedule.For(Handover, session, completedTurns: 5);

        Assert.Equal("haiku", choice.Model);
        Assert.Equal("medium", choice.Effort);
    }

    [Fact]
    public void A_chat_can_turn_off_a_handover_the_agent_would_have_done()
    {
        var session = new ChatSession { OpeningTurns = 0 };

        var choice = ModelSchedule.For(Handover, session, completedTurns: 0);

        Assert.Equal("sonnet", choice.Model);
        Assert.False(choice.IsOpening);
    }

    [Fact]
    public void A_chat_can_add_a_handover_the_agent_does_not_have()
    {
        var agent = new Agent { Model = "sonnet" };
        var session = new ChatSession { OpeningModel = "fable", OpeningTurns = 3 };

        Assert.True(ModelSchedule.For(agent, session, 2).IsOpening);
        Assert.Equal("fable", ModelSchedule.For(agent, session, 2).Model);
        Assert.False(ModelSchedule.For(agent, session, 3).IsOpening);
    }

    [Fact]
    public void Blank_settings_leave_the_cli_to_its_own_default()
    {
        var choice = ModelSchedule.For(new Agent { Model = "  " }, new ChatSession { Effort = "" }, 0);

        Assert.Null(choice.Model);
        Assert.Null(choice.Effort);
    }

    [Fact]
    public void Settings_chosen_in_a_chat_land_on_the_session()
    {
        var session = new ChatSession();

        new SessionModelSettings("sonnet", "low", "fable", "high", 2).ApplyTo(session);

        Assert.Equal("sonnet", session.Model);
        Assert.Equal("fable", session.OpeningModel);
        Assert.Equal(2, session.OpeningTurns);
    }
}
