namespace TodoMcp.Tests;

public class ClockAndAgendaTests
{
    // 2026-11-24 14:44 in Los Angeles (PST, UTC-8)
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 22, 44, 0, TimeSpan.Zero);

    [Fact]
    public void Due_date_stored_as_utc_midnight_local_reads_back_as_the_same_day()
    {
        TodoClock clock = Make.Clock(Now);
        DateTime? local = clock.ToLocal(new GraphDateTimeTimeZone { DateTime = "2026-11-25T08:00:00.0000000", TimeZone = "UTC" });
        Assert.Equal(new DateTime(2026, 11, 25, 0, 0, 0), local);
    }

    [Fact]
    public void Writes_due_dates_as_local_midnight_with_a_windows_zone_name()
    {
        GraphDateTimeTimeZone due = Make.Clock(Now).DueDate(new DateOnly(2026, 12, 1));
        Assert.Equal("2026-12-01T00:00:00", due.DateTime);
        Assert.Equal("Pacific Standard Time", due.TimeZone);
    }

    [Fact]
    public void Today_is_the_local_date_not_the_utc_date()
    {
        TodoClock clock = Make.Clock(new DateTimeOffset(2026, 11, 25, 3, 0, 0, TimeSpan.Zero)); // 19:00 on the 24th in LA
        Assert.Equal(new DateOnly(2026, 11, 24), clock.Today);
    }

    [Theory]
    [InlineData("2026-11-25T09:30")]
    [InlineData("2026-11-25 09:30")]
    public void Parses_local_reminder_times(string input) =>
        Assert.Equal(new DateTime(2026, 11, 25, 9, 30, 0), TodoClock.ParseLocalDateTime(input, "reminder"));

    [Fact]
    public void Rejects_natural_language_dates() =>
        Assert.Throws<ArgumentException>(() => TodoClock.ParseDate("tomorrow", "due_date"));

    [Fact]
    public void Agenda_sorts_tasks_into_buckets()
    {
        TodoClock clock = Make.Clock(Now);
        IEnumerable<ListedTask> tasks = new[]
        {
            Make.Task("overdue", dueUtc: "2026-11-20T08:00:00"),
            Make.Task("today", dueUtc: "2026-11-24T08:00:00"),
            Make.Task("soon", dueUtc: "2026-11-27T08:00:00"),
            Make.Task("far", dueUtc: "2027-01-10T08:00:00"),
            Make.Task("done", dueUtc: "2026-11-20T08:00:00", status: "completed"),
            Make.Task("important", importance: "high"),
            Make.Task("ping", reminderUtc: "2026-11-25T17:00:00"),
        }.Select(t => new ListedTask(t, Make.List));

        AgendaView agenda = TaskViews.BuildAgenda(tasks, clock, daysAhead: 7);

        Assert.Equal(["overdue"], agenda.Overdue.Select(t => t.Title));
        Assert.Equal(["today"], agenda.DueToday.Select(t => t.Title));
        Assert.Equal(["soon"], agenda.Upcoming.Select(t => t.Title));
        Assert.Equal(["important"], agenda.ImportantWithoutDueDate.Select(t => t.Title));
        Assert.Equal(["ping"], agenda.RemindersComingUp.Select(t => t.Title));
        Assert.Equal("2026-11-25 Wed 09:00", agenda.RemindersComingUp[0].Reminder);
        Assert.StartsWith("2026-11-24", agenda.Today);
    }
}
