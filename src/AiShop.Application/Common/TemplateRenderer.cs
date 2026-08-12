using System.Text;

namespace AiShop.Application.Common;

/// <summary>
/// Fills {{placeholder}} tokens in a prompt. Deliberately dumb — no expressions, no logic — because
/// the values come straight from workflow inputs and earlier step output.
/// </summary>
public static class TemplateRenderer
{
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template) || template.IndexOf("{{", StringComparison.Ordinal) < 0)
            return template;

        var result = new StringBuilder(template.Length);
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                result.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                result.Append(template, index, template.Length - index);
                break;
            }

            result.Append(template, index, open - index);
            var key = template[(open + 2)..close].Trim();

            if (TryResolve(values, key, out var value))
                result.Append(value);
            else
                result.Append(template, open, close + 2 - open); // Leave unknown tokens visible.

            index = close + 2;
        }

        return result.ToString();
    }

    /// <summary>Names referenced by a template, so the UI can prompt for the ones with no value.</summary>
    public static IReadOnlyList<string> FindPlaceholders(string template)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(template))
            return found;

        var index = 0;
        while (true)
        {
            var open = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
                break;

            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
                break;

            var key = template[(open + 2)..close].Trim();
            if (key.Length > 0 && !found.Contains(key, StringComparer.OrdinalIgnoreCase))
                found.Add(key);

            index = close + 2;
        }

        return found;
    }

    private static bool TryResolve(IReadOnlyDictionary<string, string> values, string key, out string value)
    {
        if (values.TryGetValue(key, out var direct))
        {
            value = direct;
            return true;
        }

        // "steps.build.output" and "steps.build" refer to the same thing.
        if (key.EndsWith(".output", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = key[..^".output".Length];
            if (values.TryGetValue(trimmed, out var stepValue))
            {
                value = stepValue;
                return true;
            }
        }

        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Lower-cased, dash-separated form of a step name, used as its context key.</summary>
    public static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}
