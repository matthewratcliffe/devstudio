using DevStudio.Ui.Services;

namespace DevStudio.Tests;

/// <summary>
/// How a generated image reaches the transcript. The renderer escapes everything first and then
/// re-introduces a small set of tags, so these are as much about what it refuses as what it renders:
/// an agent's output must never become a tag pointing off this host.
/// </summary>
public class TranscriptImageRenderingTests
{
    [Fact]
    public void Renders_markdown_image_syntax_for_a_served_image()
    {
        var html = MarkdownLite.ToHtml("![a ginger cat](/images/abc123.jpg)");

        Assert.Contains("<img class=\"md-image\" src=\"/images/abc123.jpg\"", html);
        Assert.Contains("alt=\"a ginger cat\"", html);
    }

    [Fact]
    public void Renders_a_bare_path_written_in_prose()
    {
        // What a CLI provider actually produces: it called the tool over MCP and is now describing
        // the result in its own words, without markdown.
        var html = MarkdownLite.ToHtml("Done — cat generated.\n\nImage: /images/abc123.jpg\n1024×1024");

        Assert.Contains("<img class=\"md-image\" src=\"/images/abc123.jpg\"", html);
    }

    [Fact]
    public void Offers_a_download_alongside_the_image()
    {
        var html = MarkdownLite.ToHtml("/images/abc123.jpg");

        Assert.Contains("href=\"/images/abc123.jpg?download\"", html);
        Assert.Contains("download>Download</a>", html);
    }

    [Fact]
    public void Does_not_render_the_image_tag_twice()
    {
        // The bare-path pass runs over the output of the markdown pass, so it must not match the
        // src and href the first pass just wrote.
        var html = MarkdownLite.ToHtml("![cat](/images/abc123.jpg)");

        Assert.Equal(1, Occurrences(html, "<img"));
        Assert.Equal(1, Occurrences(html, "<a class=\"md-download\""));
    }

    [Theory]
    [InlineData("![x](https://evil.example/pixel.png)")]
    [InlineData("![x](/etc/passwd)")]
    [InlineData("https://evil.example/tracker.gif")]
    [InlineData("/uploads/other.jpg")]
    public void Never_emits_an_image_tag_for_anything_this_app_does_not_serve(string markdown)
    {
        var html = MarkdownLite.ToHtml(markdown);

        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Escapes_markup_in_the_alt_text()
    {
        var html = MarkdownLite.ToHtml("![\"><script>alert(1)</script>](/images/abc123.jpg)");

        Assert.DoesNotContain("<script>", html);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;

        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
