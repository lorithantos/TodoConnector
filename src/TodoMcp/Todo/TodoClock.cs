namespace TodoMcp.Todo;

/// <summary>
/// Converts between the user's local time and Graph's dateTimeTimeZone values. The clock is a
/// required TimeProvider (house rule 7): production registers TimeProvider.System once in DI, and
/// a test that forgets its fake fails to construct rather than silently reading the real clock.
/// </summary>
public sealed class TodoClock(IOptionsMonitor<TodoMcpOptions> options, TimeProvider time)
{
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(options.CurrentValue.TimeZone);

    /// <summary>The zone id to send to Graph. Graph documents Windows names, so prefer those.</summary>
    public string GraphZoneId =>
        TimeZoneInfo.TryConvertIanaIdToWindowsId(options.CurrentValue.TimeZone, out string? windows)
            ? windows
            : options.CurrentValue.TimeZone;

    public DateTime LocalNow => TimeZoneInfo.ConvertTime(time.GetUtcNow(), this.Zone).DateTime;
    public DateOnly Today => DateOnly.FromDateTime(this.LocalNow);

    /// <summary>A Graph dateTimeTimeZone converted to the user's local wall-clock time.</summary>
    public DateTime? ToLocal(GraphDateTimeTimeZone? value)
    {
        if (value is null || string.IsNullOrEmpty(value.DateTime))
        {
            return null;
        }
        // Graph sends up to 7 fractional digits, e.g. 2026-11-25T08:00:00.0000000
        if (!DateTime.TryParse(value.DateTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            return null;
        }

        DateTime unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        TimeZoneInfo sourceZone = string.IsNullOrEmpty(value.TimeZone) ? TimeZoneInfo.Utc : FindZone(value.TimeZone);
        return TimeZoneInfo.ConvertTime(unspecified, sourceZone, this.Zone);
    }

    /// <summary>
    /// A due date as Graph expects it: midnight local time. To Do only keeps the date part.
    /// </summary>
    public GraphDateTimeTimeZone DueDate(DateOnly date) => new()
    {
        DateTime = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00",
        TimeZone = this.GraphZoneId,
    };

    public GraphDateTimeTimeZone LocalDateTime(DateTime local) => new()
    {
        DateTime = local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        TimeZone = this.GraphZoneId,
    };

    public static DateOnly ParseDate(string value, string argName) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d)
            ? d
            : throw new ArgumentException($"{argName} must be a date like 2026-11-25, got '{value}'.");

    public static DateTime ParseLocalDateTime(string value, string argName)
    {
        string[] formats = ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)
            ? d
            : throw new ArgumentException($"{argName} must be a local date and time like 2026-11-25T09:00, got '{value}'.");
    }

    private static TimeZoneInfo FindZone(string id) =>
        id.Equals("UTC", StringComparison.OrdinalIgnoreCase) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);
}
