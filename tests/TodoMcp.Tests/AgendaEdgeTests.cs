namespace TodoMcp.Tests;

/// <summary>The agenda's bucket edges that Agenda_sorts_tasks_into_buckets does not reach.</summary>
public class AgendaEdgeTests
{
    // 2026-11-24 14:44 in Los Angeles (PST, UTC-8), the same instant ClockAndAgendaTests uses.
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 22, 44, 0, TimeSpan.Zero);

    private static AgendaView Agenda(params GraphTask[] tasks) =>
        TaskViews.BuildAgenda(tasks.Select(t => new ListedTask(t, Make.List)), Make.Clock(Now), daysAhead: 7);

    [Fact]
    public void A_reminder_that_has_already_fired_is_not_coming_up()
    {
        AgendaView agenda = Agenda(
            Make.Task("fired at noon", reminderUtc: "2026-11-24T20:00:00"),
            Make.Task("tomorrow morning", reminderUtc: "2026-11-25T17:00:00"));

        Assert.Equal(["tomorrow morning"], agenda.RemindersComingUp.Select(t => t.Title));
    }

    [Fact]
    public void An_important_task_with_a_due_date_goes_in_its_date_bucket_only()
    {
        AgendaView agenda = Agenda(
            Make.Task("important and dated", dueUtc: "2026-11-27T08:00:00", importance: "high"),
            Make.Task("important and undated", importance: "high"));

        Assert.Equal(["important and dated"], agenda.Upcoming.Select(t => t.Title));
        Assert.Equal(["important and undated"], agenda.ImportantWithoutDueDate.Select(t => t.Title));
    }
}
