using DevStudio.Application.Abstractions;

namespace DevStudio.Application.Sessions;

/// <summary>What a file change looks like in the transcript: how big it was, and a few lines of it.</summary>
public readonly record struct AbridgedChange(int Added, int Removed, string Diff);

/// <summary>
/// Turns a tool's file edit into the few lines worth showing in a chat. The transcript is a
/// conversation, not a code review: a reader wants to see that a function was renamed, not the
/// whole file it lives in, so the change is cut down to the lines that actually differ and capped.
/// </summary>
public static class FileChangeSummary
{
    /// <summary>How many diff lines survive before the rest is counted rather than shown.</summary>
    public const int DefaultMaxLines = 24;

    /// <summary>Longer lines are cut, so one minified file cannot stretch the transcript.</summary>
    private const int MaxLineLength = 200;

    public static AbridgedChange Abridge(FileEdit edit, int maxLines = DefaultMaxLines)
    {
        var (removed, added) = edit.UnifiedDiff is { Length: > 0 } patch
            ? FromPatch(patch)
            : (Lines(edit.Before), Lines(edit.After));

        var body = new List<string>(removed.Count + added.Count);
        body.AddRange(removed.Select(line => "- " + Trim(line)));
        body.AddRange(added.Select(line => "+ " + Trim(line)));

        var shown = body.Count <= maxLines
            ? body
            : [.. body.Take(maxLines), $"… {body.Count - maxLines} more line{(body.Count - maxLines == 1 ? "" : "s")}"];

        return new AbridgedChange(added.Count, removed.Count, string.Join('\n', shown));
    }

    /// <summary>
    /// A unified diff already marks its own changed lines. The file headers start with the same
    /// characters, so they are dropped by their doubled form rather than by counting lines in.
    /// </summary>
    private static (List<string> Removed, List<string> Added) FromPatch(string patch)
    {
        List<string> removed = [], added = [];

        foreach (var line in patch.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))
                continue;

            if (line.StartsWith('-'))
                removed.Add(line[1..]);
            else if (line.StartsWith('+'))
                added.Add(line[1..]);
        }

        return (removed, added);
    }

    /// <summary>
    /// The text of one side of an edit. A trailing newline is the ordinary way to end a file and
    /// would otherwise show up as an empty line changing, so it is not counted.
    /// </summary>
    private static List<string> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var normalised = text.Replace("\r\n", "\n").TrimEnd('\n');

        return [.. normalised.Split('\n')];
    }

    private static string Trim(string line) =>
        line.Length <= MaxLineLength ? line : line[..MaxLineLength] + "…";
}
