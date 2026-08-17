using DevStudio.Application.Sessions;

namespace DevStudio.Tests;

public sealed class PromptGuidanceTests
{
    [Fact]
    public void Short_vague_prompt_starts_with_the_goal()
    {
        var guidance = new PromptGuidance("Help");

        Assert.Contains("accomplish", guidance.NextQuestion);
    }

    [Fact]
    public void Focused_prompt_only_asks_for_missing_context_and_output()
    {
        var guidance = new PromptGuidance("Describe caching mechanisms clearly");

        Assert.Contains("context", guidance.NextQuestion);
        guidance.AddAnswer("It is for backend developers working in this project.");
        Assert.Contains("constraints", guidance.NextQuestion);
        guidance.AddAnswer("Use a short numbered list with one practical example.");

        Assert.Null(guidance.NextQuestion);
        Assert.Contains("Goal:", guidance.BuildPrompt());
        Assert.Contains("Additional context", guidance.BuildPrompt());
    }

    [Fact]
    public void Detailed_prompt_is_ready_without_a_local_model_call()
    {
        var guidance = new PromptGuidance(
            "Summarise the API error handling in the backend project for the support team. " +
            "Use a short checklist and include the retry constraints.");

        Assert.Null(guidance.NextQuestion);
    }
}
