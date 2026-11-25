using Microsoft.Extensions.Options;
using TodoMcp.Todo;

namespace TodoMcp.Tests;

internal sealed class FixedTime(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal static class Make
{
    public static TodoClock Clock(DateTimeOffset utcNow, string zone = "America/Los_Angeles") =>
        new(new StaticOptions<TodoMcpOptions>(new TodoMcpOptions { TimeZone = zone }), new FixedTime(utcNow));

    public static GraphTask Task(string title, string? dueUtc = null, string importance = "normal",
        string status = "notStarted", string? reminderUtc = null) => new()
    {
        Id = title,
        Title = title,
        Importance = importance,
        Status = status,
        DueDateTime = dueUtc is null ? null : new GraphDateTimeTimeZone { DateTime = dueUtc, TimeZone = "UTC" },
        ReminderDateTime = reminderUtc is null ? null : new GraphDateTimeTimeZone { DateTime = reminderUtc, TimeZone = "UTC" },
        IsReminderOn = reminderUtc is not null,
    };

    public static readonly GraphTaskList List = new() { Id = "L1", DisplayName = "Tasks", WellknownListName = "defaultList" };
}
