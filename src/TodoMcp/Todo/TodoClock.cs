using System.Globalization;
using Microsoft.Extensions.Options;

namespace TodoMcp.Todo;

/// <summary>Converts between the user's local time and Graph's dateTimeTimeZone values.</summary>
public sealed class TodoClock(IOptionsMonitor<TodoMcpOptions> options, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(options.CurrentValue.TimeZone);

    /// <summary>The zone id to send to Graph. Graph documents Windows names, so prefer those.</summary>
    public string GraphZoneId =>
        TimeZoneInfo.TryConvertIanaIdToWindowsId(options.CurrentValue.TimeZone, out var windows)
            ? windows
            : options.CurrentValue.TimeZone;

    public DateTime LocalNow => TimeZoneInfo.ConvertTime(_time.GetUtcNow(), Zone).DateTime;
    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    /// <summary>A Graph dateTimeTimeZone converted to the user's local wall-clock time.</summary>
    public DateTime? ToLocal(GraphDateTimeTimeZone? value)
    {
        if (value is null || string.IsNullOrEmpty(value.DateTime)) return null;
        // Graph sends up to 7 fractional digits, e.g. 2026-11-25T08:00:00.0000000
        if (!DateTime.TryParse(value.DateTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var parsed))
            return null;
        var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        var sourceZone = string.IsNullOrEmpty(value.TimeZone) ? TimeZoneInfo.Utc : FindZone(value.TimeZone);
        return TimeZoneInfo.ConvertTime(unspecified, sourceZone, Zone);
    }

    /// <summary>
    /// A due date as Graph expects it: midnight local time. To Do only keeps the date part.
    /// </summary>
    public GraphDateTimeTimeZone DueDate(DateOnly date) => new()
    {
        DateTime = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00",
        TimeZone = GraphZoneId,
    };

    public GraphDateTimeTimeZone LocalDateTime(DateTime local) => new()
    {
        DateTime = local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        TimeZone = GraphZoneId,
    };

    public static DateOnly ParseDate(string value, string argName) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new ArgumentException($"{argName} must be a date like 2026-11-25, got '{value}'.");

    public static DateTime ParseLocalDateTime(string value, string argName)
    {
        string[] formats = ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new ArgumentException($"{argName} must be a local date and time like 2026-11-25T09:00, got '{value}'.");
    }

    private static TimeZoneInfo FindZone(string id) =>
        id.Equals("UTC", StringComparison.OrdinalIgnoreCase) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id);
}
