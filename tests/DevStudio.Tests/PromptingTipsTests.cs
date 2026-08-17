using DevStudio.Application.Sessions;
using DevStudio.Domain.Agents;
using DevStudio.Domain.Globals;

namespace DevStudio.Tests;

public sealed class PromptingTipsTests
{
    [Fact]
    public void Empty_prompt_has_no_tips()
    {
        Assert.Empty(PromptingTips.For("  "));
    }

    [Fact]
    public void Short_prompt_teaches_context_and_success_criteria()
    {
        var tips = PromptingTips.For("Fix it");

        Assert.Contains(tips, tip => tip.Contains("context"));
        Assert.Contains(tips, tip => tip.Contains("acceptance criteria"));
    }

    [Fact]
    public void Long_scoped_prompt_includes_a_cost_saving_tip()
    {
        var tips = PromptingTips.For(new string('x', 181) + " should format the result");

        Assert.Contains(tips, tip => tip.Contains("lower cost"));
    }

    [Fact]
    public void Settings_default_to_enabled_and_agents_can_inherit_or_disable()
    {
        Assert.True(new GlobalSettings().EnablePromptingTips);
        Assert.Null(new Agent().PromptingTips);
        Assert.False(new Agent { PromptingTips = false }.PromptingTips!.Value);
        Assert.False(new GlobalSettings().UseLlmForPromptingTips);
        Assert.True(new Agent { UseLlmForPromptingTips = true }.UseLlmForPromptingTips!.Value);
    }
}
