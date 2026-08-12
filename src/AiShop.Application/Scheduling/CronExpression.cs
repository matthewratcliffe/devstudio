namespace AiShop.Application.Scheduling;

/// <summary>
/// Five-field cron (minute hour day-of-month month day-of-week) with <c>*</c>, lists, ranges and
/// steps. Hand-rolled so scheduling has no third-party dependency and stays easy to unit test.
/// </summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32]; // 1-31
    private readonly bool[] _months = new bool[13];      // 1-12
    private readonly bool[] _daysOfWeek = new bool[7];   // 0 = Sunday
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    public string Expression { get; }

    private CronExpression(string expression, string[] fields)
    {
        Expression = expression;
        ParseField(fields[0], 0, 59, _minutes);
        ParseField(fields[1], 0, 23, _hours);
        ParseField(fields[2], 1, 31, _daysOfMonth);
        ParseField(fields[3], 1, 12, _months);
        ParseField(NormaliseDayOfWeek(fields[4]), 0, 6, _daysOfWeek);

        // Cron treats day-of-month and day-of-week as an OR when both are restricted.
        _dayOfMonthRestricted = fields[2].Trim() != "*";
        _dayOfWeekRestricted = fields[4].Trim() != "*";
    }

    public static CronExpression Parse(string expression)
    {
        if (!TryParse(expression, out var parsed, out var error))
            throw new FormatException(error);

        return parsed!;
    }

    public static bool TryParse(string expression, out CronExpression? parsed, out string error)
    {
        parsed = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "A cron expression is required.";
            return false;
        }

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            error = "Expected five fields: minute hour day-of-month month day-of-week.";
            return false;
        }

        try
        {
            parsed = new CronExpression(expression.Trim(), fields);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Next firing strictly after <paramref name="afterUtc"/>, evaluated in
    /// <paramref name="timeZone"/> and returned in UTC. Null when nothing matches within a year.
    /// </summary>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset afterUtc, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(afterUtc, timeZone);
        // Start from the next whole minute — cron has minute resolution.
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);
        var limit = candidate.AddYears(1);

        while (candidate < limit)
        {
            if (!_months[candidate.Month])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }

            if (!MatchesDay(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            // Skip times that do not exist because of a daylight-saving jump.
            if (timeZone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            var offset = timeZone.GetUtcOffset(candidate);
            return new DateTimeOffset(candidate, offset).ToUniversalTime();
        }

        return null;
    }

    private bool MatchesDay(DateTime candidate)
    {
        var domMatch = _daysOfMonth[candidate.Day];
        var dowMatch = _daysOfWeek[(int)candidate.DayOfWeek];

        return (_dayOfMonthRestricted, _dayOfWeekRestricted) switch
        {
            (true, true) => domMatch || dowMatch,
            (true, false) => domMatch,
            (false, true) => dowMatch,
            _ => true,
        };
    }

    private static string NormaliseDayOfWeek(string field)
    {
        // 7 and SUN both mean Sunday.
        var names = new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
        var value = field.ToUpperInvariant();
        for (var i = 0; i < names.Length; i++)
            value = value.Replace(names[i], i.ToString());

        return value.Replace("7", "0");
    }

    private static void ParseField(string field, int min, int max, bool[] target)
    {
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var step = 1;
            var range = part;

            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0)
                    throw new FormatException($"'{part}' has an invalid step.");
            }

            int from, to;
            if (range is "*" or "")
            {
                from = min;
                to = max;
            }
            else if (range.Contains('-'))
            {
                var bounds = range.Split('-', 2);
                if (!int.TryParse(bounds[0], out from) || !int.TryParse(bounds[1], out to))
                    throw new FormatException($"'{part}' is not a valid range.");
            }
            else
            {
                if (!int.TryParse(range, out from))
                    throw new FormatException($"'{part}' is not a number.");
                to = slash >= 0 ? max : from;
            }

            if (from < min || to > max || from > to)
                throw new FormatException($"'{part}' is outside the allowed range {min}-{max}.");

            for (var value = from; value <= to; value += step)
                target[value] = true;
        }
    }

    /// <summary>Plain-English-ish summary for the schedule list.</summary>
    public string Describe()
    {
        var fields = Expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields is ["*", "*", "*", "*", "*"])
            return "Every minute";
        if (fields.Length == 5 && fields[1] == "*" && fields[2] == "*" && fields[3] == "*" && fields[4] == "*")
            return $"Hourly at minute {fields[0]}";
        if (fields.Length == 5 && fields[2] == "*" && fields[3] == "*" && fields[4] == "*")
            return $"Daily at {fields[1]}:{fields[0].PadLeft(2, '0')}";

        return Expression;
    }
}
