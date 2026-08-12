using AiShop.Application.Common;

namespace AiShop.Tests;

public class TemplateRendererTests
{
    [Fact]
    public void Substitutes_declared_inputs()
    {
        var values = new Dictionary<string, string> { ["task"] = "add logging" };

        var result = TemplateRenderer.Render("Please {{task}} today.", values);

        Assert.Equal("Please add logging today.", result);
    }

    [Fact]
    public void Earlier_step_output_is_available_to_any_later_step()
    {
        var values = new Dictionary<string, string>
        {
            ["steps.implement"] = "changed Foo.cs",
            ["steps.review"] = "looks fine",
            ["previous"] = "looks fine",
        };

        var result = TemplateRenderer.Render(
            "Build said: {{steps.implement}}. Review said: {{steps.review}}. Last: {{previous}}.",
            values);

        Assert.Equal("Build said: changed Foo.cs. Review said: looks fine. Last: looks fine.", result);
    }

    [Fact]
    public void Step_output_suffix_is_optional()
    {
        var values = new Dictionary<string, string> { ["steps.build"] = "done" };

        Assert.Equal("done", TemplateRenderer.Render("{{steps.build.output}}", values));
    }

    [Fact]
    public void Unknown_placeholders_are_left_visible()
    {
        var result = TemplateRenderer.Render("Hello {{nobody}}", new Dictionary<string, string>());

        Assert.Equal("Hello {{nobody}}", result);
    }

    [Fact]
    public void Placeholder_matching_ignores_case_and_surrounding_space()
    {
        var values = new Dictionary<string, string> { ["Task"] = "ship it" };

        Assert.Equal("ship it", TemplateRenderer.Render("{{  task  }}", values));
    }

    [Fact]
    public void Finds_the_placeholders_a_template_depends_on()
    {
        var found = TemplateRenderer.FindPlaceholders("{{task}} then {{steps.review}} and {{task}} again");

        Assert.Equal(["task", "steps.review"], found);
    }

    [Theory]
    [InlineData("Build the thing", "build-the-thing")]
    [InlineData("  Review   PR #12  ", "review-pr-12")]
    [InlineData("Fix/patch", "fix-patch")]
    public void Slugify_produces_stable_context_keys(string input, string expected) =>
        Assert.Equal(expected, TemplateRenderer.Slugify(input));
}
