using DevStudio.Application.Abstractions;
using DevStudio.Application.Sessions;

namespace DevStudio.Tests;

/// <summary>
/// The transcript shows a file change in brief. What matters is that the counts match the change
/// and that nothing a tool hands over can stretch the chat out of shape.
/// </summary>
public class FileChangeSummaryTests
{
    [Fact]
    public void An_edit_shows_what_went_and_what_arrived()
    {
        var change = FileChangeSummary.Abridge(new FileEdit("src/App.cs", "var a = 1;", "var a = 2;\nvar b = 3;"));

        Assert.Equal(2, change.Added);
        Assert.Equal(1, change.Removed);
        Assert.Equal("- var a = 1;\n+ var a = 2;\n+ var b = 3;", change.Diff);
    }

    [Fact]
    public void A_new_file_is_all_addition()
    {
        var change = FileChangeSummary.Abridge(new FileEdit("notes.md", After: "one\ntwo\n"));

        Assert.Equal(2, change.Added);
        Assert.Equal(0, change.Removed);
    }

    [Fact]
    public void A_trailing_newline_is_not_a_changed_line()
    {
        var change = FileChangeSummary.Abridge(new FileEdit("a.txt", "one\n", "two\n"));

        Assert.Equal((1, 1), (change.Added, change.Removed));
    }

    [Fact]
    public void A_unified_diff_is_read_without_its_file_headers()
    {
        var patch = """
                    --- a/src/App.cs
                    +++ b/src/App.cs
                    @@ -1,3 +1,3 @@
                     unchanged
                    -gone
                    +arrived
                    """;

        var change = FileChangeSummary.Abridge(new FileEdit("src/App.cs", UnifiedDiff: patch));

        Assert.Equal((1, 1), (change.Added, change.Removed));
        Assert.DoesNotContain("a/src/App.cs", change.Diff);
        Assert.Contains("+ arrived", change.Diff);
    }

    [Fact]
    public void A_long_change_is_cut_short_but_still_counted_in_full()
    {
        var after = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"line {i}"));

        var change = FileChangeSummary.Abridge(new FileEdit("big.cs", After: after), maxLines: 4);

        Assert.Equal(100, change.Added);
        Assert.Equal(5, change.Diff.Split('\n').Length);
        Assert.Contains("96 more lines", change.Diff);
    }

    [Fact]
    public void One_very_long_line_cannot_stretch_the_transcript()
    {
        var change = FileChangeSummary.Abridge(new FileEdit("min.js", After: new string('x', 5_000)));

        Assert.True(change.Diff.Length < 300);
        Assert.EndsWith("…", change.Diff);
    }
}
