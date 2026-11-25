using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace TodoMcp.Tools;

[McpServerToolType]
public sealed class TodoTools(GraphTodoClient graph, TodoClock clock)
{
    [McpServerTool(Name = "get_agenda", ReadOnly = true, Idempotent = true)]
    [Description("""
        The nag list. Returns today's date and time, then open tasks from every list that are
        overdue, due today, due in the next days_ahead days, have a reminder coming up, or are
        marked important with no due date. Call this early in a conversation and whenever the user
        asks what they should be doing.
        """)]
    public async Task<AgendaView> GetAgenda(
        [Description("How many days ahead to look. Default 7.")] int days_ahead = 7,
        CancellationToken ct = default) => await Run(async () =>
    {
        List<GraphTaskList> lists = await graph.GetListsAsync(ct);
        List<ListedTask> all = new List<ListedTask>();
        foreach (GraphTaskList list in lists)
        {
            List<GraphTask> tasks = await graph.GetTasksAsync(list.Id, includeCompleted: false, max: 500, ct);
            all.AddRange(tasks.Select(t => new ListedTask(t, list)));
        }
        return TaskViews.BuildAgenda(all, clock, Math.Clamp(days_ahead, 0, 60));
    });

    [McpServerTool(Name = "list_task_lists", ReadOnly = true, Idempotent = true)]
    [Description("Lists the user's To Do lists (\"Tasks\" is the default list).")]
    public async Task<List<ListView>> ListTaskLists(CancellationToken ct = default) => await Run(async () =>
        (await graph.GetListsAsync(ct))
            .Select(l => new ListView(l.Id, l.DisplayName, l.WellknownListName == "defaultList", l.IsShared))
            .ToList());

    [McpServerTool(Name = "list_tasks", ReadOnly = true, Idempotent = true)]
    [Description("Lists tasks in one To Do list. Open tasks only unless include_completed is true.")]
    public async Task<List<TaskView>> ListTasks(
        [Description("List name (case-insensitive) or id. Omit for the default \"Tasks\" list.")] string? list = null,
        [Description("Also return completed tasks.")] bool include_completed = false,
        [Description("Maximum number of tasks to return. Default 50.")] int max = 50,
        CancellationToken ct = default) => await Run(async () =>
    {
        GraphTaskList target = await this.ResolveListAsync(list, ct);
        List<GraphTask> tasks = await graph.GetTasksAsync(target.Id, include_completed, Math.Clamp(max, 1, 500), ct);
        return tasks.Select(t => TaskViews.ToView(t, target, clock)).ToList();
    });

    [McpServerTool(Name = "get_task", ReadOnly = true, Idempotent = true)]
    [Description("One task with its steps (checklist items) and their ids, for add_step / update_step / delete_step.")]
    public async Task<TaskView> GetTask(
        [Description("The list id the task is in.")] string list_id,
        [Description("The task id.")] string task_id,
        CancellationToken ct = default) => await Run(async () =>
    {
        GraphTaskList list = await this.ResolveListAsync(list_id, ct);
        return await this.ReadWithStepsAsync(list, task_id, ct);
    });

    [McpServerTool(Name = "create_task")]
    [Description("""
        Adds a task to a To Do list. To make it repeat, pass repeat; a repeating task always has a
        due date, and without due_date it is due today.
        """)]
    public async Task<TaskView> CreateTask(
        [Description("Task title.")] string title,
        [Description("List name or id. Omit for the default \"Tasks\" list.")] string? list = null,
        [Description("Due date in the user's time zone, formatted yyyy-MM-dd.")] string? due_date = null,
        [Description("Reminder in the user's local time, formatted yyyy-MM-ddTHH:mm.")] string? reminder = null,
        [Description("\"low\", \"normal\" or \"high\". Default normal.")] string? importance = null,
        [Description("Plain-text notes.")] string? notes = null,
        [Description("Make it repeat: \"daily\", \"weekdays\", \"weekly\", \"monthly\" or \"yearly\". Repeats from the due date.")] string? repeat = null,
        [Description("Repeat every N days/weeks/months/years. Default 1.")] int repeat_every = 1,
        [Description("For repeat=weekly: the days, e.g. \"mon,wed\". Default: the due date's weekday.")] string? repeat_on = null,
        CancellationToken ct = default) => await Run(async () =>
    {
        RequireRepeatFor(repeat, repeat_every, repeat_on);
        GraphTaskList target = await this.ResolveListAsync(list, ct);
        Dictionary<string, object> body = new Dictionary<string, object> { ["title"] = title };
        DateOnly? due = due_date is null ? null : TodoClock.ParseDate(due_date, nameof(due_date));
        string? note = null;
        if (repeat is not null && due is null)
        {
            due = clock.Today;
            note = DueTodayNote(clock.Today);
        }

        if (due is { } dueDate)
        {
            body["dueDateTime"] = clock.DueDate(dueDate);
        }

        if (repeat is not null)
        {
            body["recurrence"] = Recurrence.Build(repeat, repeat_every, repeat_on, due!.Value);
        }

        if (reminder is not null)
        {
            body["reminderDateTime"] = clock.LocalDateTime(TodoClock.ParseLocalDateTime(reminder, nameof(reminder)));
            body["isReminderOn"] = true;
        }
        if (importance is not null)
        {
            body["importance"] = ParseImportance(importance);
        }

        if (notes is not null)
        {
            body["body"] = new GraphItemBody { Content = notes, ContentType = "text" };
        }

        GraphTask created = await graph.CreateTaskAsync(target.Id, body, ct);
        return TaskViews.ToView(created, target, clock) with { Note = note };
    });

    [McpServerTool(Name = "update_task", Idempotent = true)]
    [Description("""
        Changes a task. Only the fields you pass are changed. Use clear_due_date / clear_reminder /
        clear_recurrence to remove them, and status to reopen a completed task. To change a task,
        update it here rather than deleting and recreating it.
        """)]
    public async Task<TaskView> UpdateTask(
        [Description("The list id the task is in (from a previous result).")] string list_id,
        [Description("The task id.")] string task_id,
        [Description("New title.")] string? title = null,
        [Description("New due date, yyyy-MM-dd.")] string? due_date = null,
        [Description("New reminder, local time, yyyy-MM-ddTHH:mm.")] string? reminder = null,
        [Description("\"low\", \"normal\" or \"high\".")] string? importance = null,
        [Description("Replaces the notes.")] string? notes = null,
        [Description("\"notStarted\", \"inProgress\", \"completed\", \"waitingOnOthers\" or \"deferred\".")] string? status = null,
        [Description("Remove the due date.")] bool clear_due_date = false,
        [Description("Turn the reminder off.")] bool clear_reminder = false,
        [Description("Make it repeat: \"daily\", \"weekdays\", \"weekly\", \"monthly\" or \"yearly\". Repeats from the due date.")] string? repeat = null,
        [Description("Repeat every N days/weeks/months/years. Default 1.")] int repeat_every = 1,
        [Description("For repeat=weekly: the days, e.g. \"mon,wed\". Default: the due date's weekday.")] string? repeat_on = null,
        [Description("Stop the task repeating.")] bool clear_recurrence = false,
        CancellationToken ct = default) => await Run(async () =>
    {
        RequireRepeatFor(repeat, repeat_every, repeat_on);
        if (repeat is not null && clear_recurrence)
        {
            throw new ArgumentException("Pass repeat or clear_recurrence, not both.");
        }

        if (repeat is not null && clear_due_date)
        {
            throw new ArgumentException("A repeating task needs a due date: drop clear_due_date, or pass clear_recurrence instead of repeat.");
        }

        Dictionary<string, object?> patch = new Dictionary<string, object?>();
        if (title is not null)
        {
            patch["title"] = title;
        }

        if (clear_due_date)
        {
            patch["dueDateTime"] = null;
        }
        else if (due_date is not null)
        {
            patch["dueDateTime"] = clock.DueDate(TodoClock.ParseDate(due_date, nameof(due_date)));
        }

        if (clear_reminder)
        {
            patch["isReminderOn"] = false;
            patch["reminderDateTime"] = null;
        }
        else if (reminder is not null)
        {
            patch["reminderDateTime"] = clock.LocalDateTime(TodoClock.ParseLocalDateTime(reminder, nameof(reminder)));
            patch["isReminderOn"] = true;
        }
        if (importance is not null)
        {
            patch["importance"] = ParseImportance(importance);
        }

        if (notes is not null)
        {
            patch["body"] = new GraphItemBody { Content = notes, ContentType = "text" };
        }

        if (status is not null)
        {
            patch["status"] = ParseStatus(status);
        }

        if (clear_recurrence)
        {
            patch["recurrence"] = null;
        }

        if (patch.Count == 0 && repeat is null)
        {
            throw new ArgumentException("Nothing to change: pass at least one field.");
        }

        GraphTaskList list = await this.ResolveListAsync(list_id, ct);
        string? note = null;
        if (repeat is not null)
        {
            // The pattern repeats from the due date: the one passed, else the task's own, else today.
            DateOnly? start = due_date is null ? null : TodoClock.ParseDate(due_date, nameof(due_date));
            if (start is null)
            {
                GraphTask current = await graph.GetTaskAsync(list.Id, task_id, ct);
                start = clock.ToLocal(current.DueDateTime) is { } existing ? DateOnly.FromDateTime(existing) : null;
            }

            if (start is null)
            {
                start = clock.Today;
                patch["dueDateTime"] = clock.DueDate(clock.Today);
                note = DueTodayNote(clock.Today);
            }

            patch["recurrence"] = Recurrence.Build(repeat, repeat_every, repeat_on, start.Value);
        }

        GraphTask updated = await graph.UpdateTaskAsync(list.Id, task_id, patch, ct);
        return TaskViews.ToView(updated, list, clock) with { Note = note };
    });

    [McpServerTool(Name = "complete_task", Idempotent = true)]
    [Description("Marks a task as completed.")]
    public async Task<TaskView> CompleteTask(
        [Description("The list id the task is in.")] string list_id,
        [Description("The task id.")] string task_id,
        CancellationToken ct = default) => await Run(async () =>
    {
        GraphTaskList list = await this.ResolveListAsync(list_id, ct);
        GraphTask updated = await graph.UpdateTaskAsync(list.Id, task_id, new { status = "completed" }, ct);
        return TaskViews.ToView(updated, list, clock);
    });

    [McpServerTool(Name = "create_task_list")]
    [Description("Creates a new To Do list.")]
    public async Task<ListView> CreateTaskList(
        [Description("Name of the new list.")] string name,
        CancellationToken ct = default) => await Run(async () =>
    {
        GraphTaskList created = await graph.CreateListAsync(name, ct);
        return new ListView(created.Id, created.DisplayName, false, created.IsShared);
    });

    [McpServerTool(Name = "rename_task_list", Idempotent = true)]
    [Description("Renames a To Do list. The built-in \"Tasks\" and \"Flagged email\" lists cannot be renamed.")]
    public async Task<ListView> RenameTaskList(
        [Description("List name (case-insensitive) or id.")] string list,
        [Description("The new name.")] string new_name,
        CancellationToken ct = default) => await Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(new_name))
        {
            throw new ArgumentException("new_name must not be empty.");
        }

        GraphTaskList target = await this.ResolveListAsync(list, ct);
        RefuseBuiltIn(target, "renamed");
        GraphTaskList renamed = await graph.RenameListAsync(target.Id, new_name.Trim(), ct);
        return new ListView(renamed.Id, renamed.DisplayName, false, renamed.IsShared);
    });

    [McpServerTool(Name = "delete_task_list", Destructive = true)]
    [Description("""
        Deletes a To Do list and every task in it. Cannot be undone. Confirm with the user first,
        naming the list, unless they named it themselves in this request. The built-in "Tasks" and
        "Flagged email" lists cannot be deleted.
        """)]
    public async Task<DeletedListView> DeleteTaskList(
        [Description("List name (case-insensitive) or id.")] string list,
        CancellationToken ct = default) => await Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            throw new ArgumentException("Name the list to delete.");
        }

        GraphTaskList target = await this.ResolveListAsync(list, ct);
        RefuseBuiltIn(target, "deleted");
        List<GraphTask> open = await graph.GetTasksAsync(target.Id, includeCompleted: false, max: int.MaxValue, ct);
        await graph.DeleteListAsync(target.Id, ct);
        return new DeletedListView(target.Id, target.DisplayName, open.Count);
    });

    [McpServerTool(Name = "delete_task", Destructive = true)]
    [Description("""
        Deletes a task and its steps. Cannot be undone. Confirm with the user first, naming the task,
        unless they named it themselves in this request. To change a task, use update_task instead;
        to finish one, complete_task.
        """)]
    public async Task<DeletedTaskView> DeleteTask(
        [Description("The list id the task is in.")] string list_id,
        [Description("The task id.")] string task_id,
        CancellationToken ct = default) => await Run(async () =>
    {
        GraphTaskList list = await this.ResolveListAsync(list_id, ct);
        GraphTask doomed = await graph.GetTaskAsync(list.Id, task_id, ct);
        await graph.DeleteTaskAsync(list.Id, doomed.Id, ct);
        return new DeletedTaskView(list.Id, list.DisplayName, doomed.Id, doomed.Title);
    });

    [McpServerTool(Name = "move_task", Destructive = true)]
    [Description("""
        Moves a task, with its steps, to another list. Graph has no move, so this copies the task
        into the other list and then deletes the original; the task gets a new id. Confirm with the
        user first, naming the task and both lists, unless they named them in this request.
        """)]
    public async Task<TaskView> MoveTask(
        [Description("The list id the task is in now.")] string list_id,
        [Description("The task id.")] string task_id,
        [Description("The list to move it to: name (case-insensitive) or id.")] string to_list,
        CancellationToken ct = default) => await Run(async () =>
    {
        GraphTaskList from = await this.ResolveListAsync(list_id, ct);
        GraphTaskList to = await this.ResolveListAsync(to_list, ct);
        if (from.Id == to.Id)
        {
            throw new ArgumentException($"The task is already in '{from.DisplayName}'; name a different list.");
        }

        GraphTask original = await graph.GetTaskAsync(from.Id, task_id, ct);
        List<GraphChecklistItem> steps = await graph.GetStepsAsync(from.Id, original.Id, ct);
        GraphTask copy = await graph.CreateTaskAsync(to.Id, CopyOf(original), ct);

        // From here on the task exists twice. The original is deleted only after the copy and
        // every step exist, and any failure says exactly what is where.
        List<GraphChecklistItem> copiedSteps = new List<GraphChecklistItem>();
        try
        {
            foreach (GraphChecklistItem step in steps)
            {
                object stepBody = new { displayName = step.DisplayName, isChecked = step.IsChecked };
                copiedSteps.Add(await graph.CreateStepAsync(to.Id, copy.Id, stepBody, ct));
            }
        }
        catch (Exception stepFailure)
        {
            throw new McpException(
                $"Move incomplete: the copy was created in '{to.DisplayName}' as task {copy.Id} but its steps failed " +
                $"after {copiedSteps.Count} of {steps.Count}: {stepFailure.Message} The original in '{from.DisplayName}' " +
                $"(task {original.Id}) was kept, so '{original.Title}' is now in both lists.");
        }

        try
        {
            await graph.DeleteTaskAsync(from.Id, original.Id, ct);
        }
        catch (Exception deleteFailure)
        {
            throw new McpException(
                $"Move incomplete: the copy was created in '{to.DisplayName}' as task {copy.Id} with all " +
                $"{steps.Count} steps, but deleting the original in '{from.DisplayName}' (task {original.Id}) " +
                $"failed: {deleteFailure.Message} '{original.Title}' is now in both lists.");
        }

        return TaskViews.ToView(copy, to, clock, copiedSteps) with
        {
            Note = $"Moved from '{from.DisplayName}'; it has a new id in '{to.DisplayName}'.",
        };
    });

    [McpServerTool(Name = "add_step")]
    [Description("Adds a step (checklist item) to a task. Returns the task with all its steps.")]
    public async Task<TaskView> AddStep(
        [Description("The list id the task is in.")] string list_id,
        [Description("The task id.")] string task_id,
        [Description("The step's text.")] string text,
        CancellationToken ct = default) => await Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("text must not be empty.");
        }

        GraphTaskList list = await this.ResolveListAsync(list_id, ct);
        await graph.CreateStepAsync(list.Id, task_id, new { displayName = text.Trim() }, ct);
        return await this.ReadWithStepsAsync(list, task_id, ct);
    });

    [McpServerTool(Name = "update_step", Idempotent = true)]
    [Description("Changes a step's text, or ticks it off (done=true) or back on (done=false). Returns the task with all its steps.")]
    public async Task<TaskView> UpdateStep(
        [Description("The list id the task is in.")] string list_id,
        [Description("The task id.")] string task_id,
        [Description("The step id (from get_task).")] string step_id,
        [Description("New text.")] string? text = null,
        [Description("true to tick the step off, false to untick it.")] bool? done = null,
        CancellationToken ct = default) => await Run(async () =>
    {
        Dictionary<string, object> patch = new Dictionary<string, object>();
        if (text is not null)
        {
            patch["displayName"] = text.Trim();
        }

        if (done is { } isChecked)
        {
            patch["isChecked"] = isChecked;
        }

        if (patch.Count == 0)
        {
            throw new ArgumentException("Nothing to change: pass text or done.");
        }

        GraphTaskList list = await this.ResolveListAsync(list_id, ct);
        await graph.UpdateStepAsync(list.Id, task_id, step_id, patch, ct);
        return await this.ReadWithStepsAsync(list, task_id, ct);
    });

    [McpServerTool(Name = "delete_step", Destructive = true)]
    [Description("Removes a step from a task. Returns the task with the steps left.")]
    public async Task<TaskView> DeleteStep(
        [Description("The list id the task is in.")] string list_id,
        [Description("The task id.")] string task_id,
        [Description("The step id (from get_task).")] string step_id,
        CancellationToken ct = default) => await Run(async () =>
    {
        GraphTaskList list = await this.ResolveListAsync(list_id, ct);
        await graph.DeleteStepAsync(list.Id, task_id, step_id, ct);
        return await this.ReadWithStepsAsync(list, task_id, ct);
    });

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<TaskView> ReadWithStepsAsync(GraphTaskList list, string taskId, CancellationToken ct)
    {
        GraphTask task = await graph.GetTaskAsync(list.Id, taskId, ct);
        List<GraphChecklistItem> steps = await graph.GetStepsAsync(list.Id, task.Id, ct);
        return TaskViews.ToView(task, list, clock, steps);
    }

    /// <summary>
    /// The create body for move_task's copy: every field the tool promises to carry, taken as Graph
    /// returned it (todoTask properties: https://learn.microsoft.com/graph/api/resources/todotask?view=graph-rest-1.0).
    /// </summary>
    private static Dictionary<string, object> CopyOf(GraphTask t)
    {
        Dictionary<string, object> body = new Dictionary<string, object>
        {
            ["title"] = t.Title,
            ["importance"] = t.Importance,
            ["status"] = t.Status,
            ["isReminderOn"] = t.IsReminderOn,
        };
        if (t.Body is not null)
        {
            body["body"] = t.Body;
        }

        if (t.DueDateTime is not null)
        {
            body["dueDateTime"] = t.DueDateTime;
        }

        if (t.ReminderDateTime is not null)
        {
            body["reminderDateTime"] = t.ReminderDateTime;
        }

        if (t.Recurrence is not null)
        {
            body["recurrence"] = Recurrence.Normalize(t.Recurrence);
        }

        if (t.Categories is { Count: > 0 })
        {
            body["categories"] = t.Categories;
        }

        return body;
    }

    /// <summary>
    /// "In To Do, there are built-in task lists such as Flagged emails and Tasks which cannot be
    /// renamed or deleted." -- https://learn.microsoft.com/graph/api/resources/todotasklist?view=graph-rest-1.0.
    /// A user's own list has wellknownListName "none".
    /// </summary>
    private static void RefuseBuiltIn(GraphTaskList list, string verb)
    {
        if (list.WellknownListName is not null && list.WellknownListName != "none")
        {
            throw new ArgumentException(
                $"'{list.DisplayName}' is a built-in To Do list ({list.WellknownListName}) and cannot be {verb}.");
        }
    }

    private static void RequireRepeatFor(string? repeat, int repeatEvery, string? repeatOn)
    {
        if (repeat is null && (repeatEvery != 1 || repeatOn is not null))
        {
            throw new ArgumentException($"repeat_every and repeat_on need repeat ({Recurrence.RepeatChoices}).");
        }
    }

    private static string DueTodayNote(DateOnly today) =>
        $"A repeating task needs a due date and none was given, so it is due today ({today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}) and repeats from there.";

    private async Task<GraphTaskList> ResolveListAsync(string? nameOrId, CancellationToken ct)
    {
        List<GraphTaskList> lists = await graph.GetListsAsync(ct);
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return lists.FirstOrDefault(l => l.WellknownListName == "defaultList")
                ?? lists.FirstOrDefault()
                ?? throw new McpException("No To Do lists found.");
        }

        return lists.FirstOrDefault(l => l.Id == nameOrId)
            ?? lists.FirstOrDefault(l => string.Equals(l.DisplayName, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(
                $"No list named '{nameOrId}'. Lists: {string.Join(", ", lists.Select(l => l.DisplayName))}.");
    }

    private static string ParseImportance(string value) => value.Trim().ToLowerInvariant() switch
    {
        "low" or "normal" or "high" => value.Trim().ToLowerInvariant(),
        _ => throw new ArgumentException("importance must be low, normal or high."),
    };

    private static string ParseStatus(string value)
    {
        string[] allowed = ["notStarted", "inProgress", "completed", "waitingOnOthers", "deferred"];
        return allowed.FirstOrDefault(a => a.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"status must be one of {string.Join(", ", allowed)}.");
    }

    /// <summary>Turns expected failures into messages Claude can show the user.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
        catch (GraphRequestException e) { throw new McpException(e.Message); }
        catch (MicrosoftIdentityWebChallengeUserException e)
        {
            throw new McpException(
                "Couldn't get a Microsoft Graph token for this user. Disconnect and reconnect the connector " +
                $"in Claude to sign in and consent again. ({e.MsalUiRequiredException.ErrorCode})");
        }
    }
}
