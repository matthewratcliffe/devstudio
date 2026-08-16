using System.Globalization;
using System.Text.RegularExpressions;

namespace DevStudio.Application.Queues;

/// <summary>Turns provider usage-limit messages into a queue pause.</summary>
public static partial class UsageLimitBackoff
{
    private static readonly string[] Markers =
    [
        "usage limit", "usage exceeded", "out of usage", "rate limit", "too many requests",
        "capacity", "overloaded"
    ];

    public static bool TryGetUntil(string? message, DateTimeOffset now, out DateTimeOffset until)
    {
        until = default;
        if (string.IsNullOrWhiteSpace(message) ||
            !Markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return false;

        var match = ResetTime().Match(message);
        if (match.Success &&
            match.Groups["minute"].Value is var minute &&
            DateTime.TryParseExact(
                $"{match.Groups["hour"].Value}:{(string.IsNullOrEmpty(minute) ? "00" : minute)} {match.Groups["ampm"].Value}",
                ["h:m tt", "hh:mm tt"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            // The reset time is reported in the same wall-clock context as the
            // timestamp supplied by the caller. Do not use the host machine's
            // local timezone; CI and production hosts may run in UTC.
            var reset = new DateTimeOffset(now.Date.Add(parsed.TimeOfDay), now.Offset);
            if (reset <= now)
                reset = reset.AddDays(1);

            until = reset.AddMinutes(1);
            return true;
        }

        until = now.AddHours(2);
        return true;
    }

    [GeneratedRegex(@"(?:reset|resets|available|try again)[^\r\n]{0,40}?\b(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>a\.?m\.?|p\.?m\.?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResetTime();
}
