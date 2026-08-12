using System.Text;
using System.Text.RegularExpressions;

namespace DevStudio.Ui.Services;

/// <summary>
/// Just enough Markdown for agent transcripts — fenced code, inline code, bold, italics, headings,
/// lists and links. Everything is HTML-escaped first, so CLI output can never inject markup.
/// </summary>
public static partial class MarkdownLite
{
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var html = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCode = false;
        var inList = false;

        foreach (var raw in lines)
        {
            var line = raw;

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    html.Append("</code></pre>");
                    inCode = false;
                }
                else
                {
                    CloseList(html, ref inList);
                    var language = line.Trim()[3..].Trim();
                    html.Append($"<pre data-lang=\"{Escape(language)}\"><code>");
                    inCode = true;
                }

                continue;
            }

            if (inCode)
            {
                html.Append(Escape(line)).Append('\n');
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                CloseList(html, ref inList);
                continue;
            }

            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                CloseList(html, ref inList);
                html.Append("<h4>").Append(Inline(trimmed[4..])).Append("</h4>");
            }
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                CloseList(html, ref inList);
                html.Append("<h3>").Append(Inline(trimmed[3..])).Append("</h3>");
            }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                CloseList(html, ref inList);
                html.Append("<h3>").Append(Inline(trimmed[2..])).Append("</h3>");
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                OpenList(html, ref inList);
                html.Append("<li>").Append(Inline(trimmed[2..])).Append("</li>");
            }
            else if (NumberedItem().IsMatch(trimmed))
            {
                OpenList(html, ref inList);
                html.Append("<li>").Append(Inline(NumberedItem().Replace(trimmed, string.Empty, 1))).Append("</li>");
            }
            else
            {
                CloseList(html, ref inList);
                html.Append("<p>").Append(Inline(line)).Append("</p>");
            }
        }

        if (inCode)
            html.Append("</code></pre>");

        CloseList(html, ref inList);
        return html.ToString();
    }

    private static void OpenList(StringBuilder html, ref bool inList)
    {
        if (inList)
            return;

        html.Append("<ul>");
        inList = true;
    }

    private static void CloseList(StringBuilder html, ref bool inList)
    {
        if (!inList)
            return;

        html.Append("</ul>");
        inList = false;
    }

    private static string Inline(string text)
    {
        var escaped = Escape(text);

        // Images, but only ones this app serves. Restricting the pattern to /images/ is what keeps
        // the promise made at the top of the file: an agent cannot talk the renderer into emitting a
        // tag that points anywhere else, so there is no callback to an attacker's host.
        escaped = Image().Replace(escaped, m => Figure(m.Groups[2].Value, m.Groups[1].Value));

        // The same path written as plain prose. A CLI provider generates through MCP and then
        // describes the result in its own words — "Image: /images/x.jpg" — so waiting for markdown
        // syntax means usually showing a path where a picture belongs. The lookbehind keeps this off
        // the src and href of the tag the line above just produced.
        escaped = BareImagePath().Replace(escaped, m => Figure(m.Groups[1].Value, string.Empty));

        escaped = InlineCode().Replace(escaped, "<code>$1</code>");
        escaped = Bold().Replace(escaped, "<strong>$1</strong>");
        escaped = Italic().Replace(escaped, "<em>$1</em>");
        escaped = Link().Replace(escaped, "<a href=\"$2\" target=\"_blank\" rel=\"noopener noreferrer\">$1</a>");
        escaped = BareUrl().Replace(escaped, "<a href=\"$1\" target=\"_blank\" rel=\"noopener noreferrer\">$1</a>");
        return escaped;
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\w*])\*([^*\n]+)\*(?![\w*])")]
    private static partial Regex Italic();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"!\[([^\]]*)\]\((/images/[A-Za-z0-9._%-]+)\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"(?<![=""'/\w])(/images/[A-Za-z0-9._%-]+\.[A-Za-z]{3,4})")]
    private static partial Regex BareImagePath();

    /// <summary>
    /// The picture plus a way to keep it. Both are worth having in a transcript: the image answers
    /// "what did it draw", and the link answers "can I have it" without a right-click.
    /// </summary>
    private static string Figure(string url, string alt) =>
        $"""
         <span class="md-figure"><img class="md-image" src="{url}" alt="{alt}" loading="lazy" />
         <a class="md-download" href="{url}?download" download>Download</a></span>
         """;

    [GeneratedRegex(@"(?<!["")>=])(https?://[^\s<""]+)")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"^\d+\.\s+")]
    private static partial Regex NumberedItem();
}
