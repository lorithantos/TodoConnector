using System.Collections.Concurrent;

namespace TodoMcp.Tests;

internal sealed class FixedTime(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

/// <summary>Captures formatted log lines so a test can read what an operator would see.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        this.Lines.Add(formatter(state, exception));
}

/// <summary>One log line an app under test wrote, with its level and category.</summary>
public sealed record LogLine(LogLevel Level, string Category, string Message);

/// <summary>
/// A logger provider for a WebApplicationFactory: every line the app logs, from any request thread,
/// lands in <see cref="Lines"/>, so a test can read what the App Service log would show.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<LogLine> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Writer(categoryName, this.Lines);

    public IReadOnlyList<LogLine> Warnings() => this.Lines.Where(l => l.Level == LogLevel.Warning).ToList();

    public void Dispose()
    {
    }

    private sealed class Writer(string category, ConcurrentQueue<LogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(new LogLine(logLevel, category, formatter(state, exception)));
    }
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
