namespace TodoMcp.Todo;

// What the tools return to Claude: flat, local-time, easy to read.

/// <summary>One open task and the list it came from: the agenda's input.</summary>
public sealed record ListedTask(GraphTask Task, GraphTaskList List);

/// <summary>A listed task with its due date already read in the user's time zone.</summary>
internal sealed record DatedTask(GraphTask Task, GraphTaskList List, DateOnly? Due);

public sealed record ListView(string Id, string Name, bool IsDefault, bool IsShared);

public sealed record TaskView(
    string ListId,
    string ListName,
    string Id,
    string Title,
    string Status,
    string Importance,
    string? Due,
    string? Reminder,
    string? Notes,
    string? Completed,
    string? Repeats,
    List<StepView>? Steps,
    string? Note = null);

/// <summary>One step (Graph checklistItem) of a task.</summary>
public sealed record StepView(string Id, string Text, bool Done);

/// <summary>What delete_task removed, so Claude can say it.</summary>
public sealed record DeletedTaskView(string ListId, string ListName, string Id, string Title);

/// <summary>What delete_task_list removed, and how many open tasks went with it.</summary>
public sealed record DeletedListView(string Id, string Name, int OpenTasks);

public sealed record AgendaView(
    string Today,
    string Now,
    string TimeZone,
    List<TaskView> Overdue,
    List<TaskView> DueToday,
    List<TaskView> Upcoming,
    List<TaskView> RemindersComingUp,
    List<TaskView> ImportantWithoutDueDate);

public static class TaskViews
{
    /// <summary>
    /// A task as Claude sees it. Steps are null unless the caller read them: Graph's v1.0 docs
    /// for listing and getting tasks do not document $expand=checklistItems, so steps come from
    /// their own request, made only where one task is returned.
    /// </summary>
    public static TaskView ToView(GraphTask t, GraphTaskList list, TodoClock clock, List<GraphChecklistItem>? steps = null)
    {
        DateTime? due = clock.ToLocal(t.DueDateTime);
        DateTime? reminder = t.IsReminderOn ? clock.ToLocal(t.ReminderDateTime) : null;
        DateTime? completed = clock.ToLocal(t.CompletedDateTime);
        string? notes = t.Body?.Content;
        return new TaskView(
            list.Id, list.DisplayName, t.Id, t.Title, t.Status, t.Importance,
            due?.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture),
            reminder?.ToString("yyyy-MM-dd ddd HH:mm", CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            completed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Recurrence.Describe(t.Recurrence),
            steps?.Select(s => new StepView(s.Id, s.DisplayName, s.IsChecked)).ToList());
    }

    /// <summary>Sorts open tasks into the buckets Claude nags about.</summary>
    public static AgendaView BuildAgenda(
        IEnumerable<ListedTask> tasks, TodoClock clock, int daysAhead)
    {
        DateOnly today = clock.Today;
        DateTime now = clock.LocalNow;
        DateOnly horizon = today.AddDays(daysAhead);
        DateTime reminderHorizon = now.AddDays(daysAhead);

        List<TaskView> overdue = [], dueToday = [], upcoming = [], reminders = [], important = [];
        IOrderedEnumerable<DatedTask> ordered = tasks
            .Where(x => !x.Task.IsCompleted)
            .Select(x => new DatedTask(x.Task, x.List, clock.ToLocal(x.Task.DueDateTime) is { } d ? DateOnly.FromDateTime(d) : null))
            .OrderBy(x => x.Due ?? DateOnly.MaxValue)
            .ThenByDescending(x => x.Task.Importance == "high");

        foreach ((GraphTask task, GraphTaskList list, DateOnly? due) in ordered)
        {
            TaskView view = ToView(task, list, clock);
            if (due is { } d)
            {
                if (d < today)
                {
                    overdue.Add(view);
                }
                else if (d == today)
                {
                    dueToday.Add(view);
                }
                else if (d <= horizon)
                {
                    upcoming.Add(view);
                }
            }
            else if (string.Equals(task.Importance, "high", StringComparison.OrdinalIgnoreCase))
            {
                important.Add(view);
            }

            if (task.IsReminderOn && clock.ToLocal(task.ReminderDateTime) is { } r && r >= now && r <= reminderHorizon)
            {
                reminders.Add(view);
            }
        }

        return new AgendaView(
            today.ToString("yyyy-MM-dd dddd", CultureInfo.InvariantCulture),
            now.ToString("HH:mm", CultureInfo.InvariantCulture),
            clock.Zone.Id,
            overdue, dueToday, upcoming,
            reminders.OrderBy(v => v.Reminder, StringComparer.Ordinal).ToList(),
            important);
    }
}
