namespace TodoMcp.Todo;

/// <summary>
/// Maps the tools' plain repeat options to Graph's patternedRecurrence and back to a short
/// sentence. Required members per pattern type follow the table on
/// https://learn.microsoft.com/graph/api/resources/recurrencepattern?view=graph-rest-1.0
/// (weekly needs daysOfWeek and firstDayOfWeek, absoluteMonthly dayOfMonth, absoluteYearly
/// dayOfMonth and month), and the range follows
/// https://learn.microsoft.com/graph/api/resources/recurrencerange?view=graph-rest-1.0
/// (startDate always required; noEnd needs nothing more).
/// </summary>
public static class Recurrence
{
    public const string RepeatChoices = "daily, weekdays, weekly, monthly or yearly";

    // Graph's dayOfWeek values in week order; FromDayOfWeek indexes this by DayOfWeek.
    private static readonly string[] Days = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
    private static readonly string[] Weekdays = ["monday", "tuesday", "wednesday", "thursday", "friday"];

    /// <summary>
    /// The recurrence for a task that repeats from <paramref name="start"/>, its due date.
    /// Throws ArgumentException with a message for the user on anything it cannot map.
    /// </summary>
    public static GraphRecurrence Build(string repeat, int every, string? repeatOn, DateOnly start)
    {
        string kind = repeat.Trim().ToLowerInvariant();
        if (every < 1)
        {
            throw new ArgumentException($"repeat_every must be 1 or more, got {every}.");
        }

        if (repeatOn is not null && kind != "weekly")
        {
            throw new ArgumentException("repeat_on only applies to repeat=weekly.");
        }

        GraphRecurrencePattern pattern = kind switch
        {
            "daily" => new GraphRecurrencePattern { Type = "daily", Interval = every },
            "weekdays" => new GraphRecurrencePattern
            {
                Type = "weekly", Interval = every, DaysOfWeek = [.. Weekdays], FirstDayOfWeek = "sunday",
            },
            "weekly" => new GraphRecurrencePattern
            {
                Type = "weekly",
                Interval = every,
                DaysOfWeek = repeatOn is null ? [Days[(int)start.DayOfWeek]] : ParseDays(repeatOn),
                FirstDayOfWeek = "sunday",
            },
            "monthly" => new GraphRecurrencePattern { Type = "absoluteMonthly", Interval = every, DayOfMonth = start.Day },
            "yearly" => new GraphRecurrencePattern
            {
                Type = "absoluteYearly", Interval = every, DayOfMonth = start.Day, Month = start.Month,
            },
            _ => throw new ArgumentException($"repeat must be {RepeatChoices}, got '{repeat}'."),
        };

        return new GraphRecurrence
        {
            Pattern = pattern,
            Range = new GraphRecurrenceRange { Type = "noEnd", StartDate = Date(start) },
        };
    }

    /// <summary>
    /// A copy of a recurrence Graph returned, keeping only the members its pattern and range
    /// types need. Graph reads back every member with a default (dayOfMonth 0 on a weekly
    /// pattern), and recurrencePattern's docs say an unsupported value is an error.
    /// </summary>
    public static GraphRecurrence Normalize(GraphRecurrence source)
    {
        GraphRecurrencePattern p = source.Pattern;
        bool weekly = p.Type == "weekly";
        bool relative = p.Type is "relativeMonthly" or "relativeYearly";
        bool absolute = p.Type is "absoluteMonthly" or "absoluteYearly";
        bool yearly = p.Type is "absoluteYearly" or "relativeYearly";
        GraphRecurrencePattern pattern = new GraphRecurrencePattern
        {
            Type = p.Type,
            Interval = p.Interval,
            DaysOfWeek = weekly || relative ? p.DaysOfWeek : null,
            FirstDayOfWeek = weekly ? p.FirstDayOfWeek ?? "sunday" : null,
            DayOfMonth = absolute ? p.DayOfMonth : null,
            Month = yearly ? p.Month : null,
            Index = relative ? p.Index : null,
        };

        GraphRecurrenceRange r = source.Range;
        GraphRecurrenceRange range = new GraphRecurrenceRange
        {
            Type = r.Type,
            StartDate = r.StartDate,
            EndDate = r.Type == "endDate" ? r.EndDate : null,
            NumberOfOccurrences = r.Type == "numbered" ? r.NumberOfOccurrences : null,
            RecurrenceTimeZone = string.IsNullOrEmpty(r.RecurrenceTimeZone) ? null : r.RecurrenceTimeZone,
        };
        return new GraphRecurrence { Pattern = pattern, Range = range };
    }

    /// <summary>A short sentence such as "every 2 weeks on Mon, Wed"; null when the task does not repeat.</summary>
    public static string? Describe(GraphRecurrence? recurrence)
    {
        if (recurrence is null)
        {
            return null;
        }

        GraphRecurrencePattern p = recurrence.Pattern;
        string? unit = p.Type switch
        {
            "daily" => "day",
            "weekly" => "week",
            "absoluteMonthly" or "relativeMonthly" => "month",
            "absoluteYearly" or "relativeYearly" => "year",
            _ => null,
        };
        if (unit is null)
        {
            return $"repeats ({p.Type})";
        }

        string every = p.Interval <= 1 ? $"every {unit}" : $"every {p.Interval} {unit}s";
        List<string> days = p.DaysOfWeek ?? [];
        string dayList = string.Join(", ", Days.Where(d => days.Contains(d, StringComparer.OrdinalIgnoreCase)).Select(Short));
        bool isWeekdays = days.Count == Weekdays.Length && Weekdays.All(d => days.Contains(d, StringComparer.OrdinalIgnoreCase));
        return p.Type switch
        {
            "weekly" when isWeekdays => p.Interval <= 1 ? "every weekday" : $"{every} on weekdays",
            "weekly" => $"{every} on {dayList}",
            "absoluteMonthly" => $"{every} on day {p.DayOfMonth}",
            "absoluteYearly" => $"{every} on {MonthName(p.Month)} {p.DayOfMonth}",
            "relativeMonthly" => $"{every} on the {p.Index ?? "first"} {dayList}",
            "relativeYearly" => $"{every} on the {p.Index ?? "first"} {dayList} of {MonthName(p.Month)}",
            _ => every,
        };
    }

    private static List<string> ParseDays(string value)
    {
        string[] tokens = value.Split([',', ' ', ';', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw new ArgumentException("repeat_on needs at least one day, like mon,wed,fri.");
        }

        HashSet<string> chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in tokens)
        {
            string t = token.Trim().ToLowerInvariant();
            string day = t.Length >= 2 ? Days.FirstOrDefault(d => d.StartsWith(t, StringComparison.Ordinal)) ?? "" : "";
            if (day.Length == 0)
            {
                throw new ArgumentException($"repeat_on: '{token}' is not a day of the week; use names like mon,wed,fri.");
            }

            chosen.Add(day);
        }

        return Days.Where(chosen.Contains).ToList();
    }

    private static string Short(string day) => char.ToUpperInvariant(day[0]) + day[1..3];

    private static string MonthName(int? month) =>
        month is >= 1 and <= 12
            ? CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month.Value)
            : "?";

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
