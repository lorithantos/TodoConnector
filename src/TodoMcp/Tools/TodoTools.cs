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

    [McpServerTool(Name = "create_task")]
    [Description("Adds a task to a To Do list.")]
    public async Task<TaskView> CreateTask(
        [Description("Task title.")] string title,
        [Description("List name or id. Omit for the default \"Tasks\" list.")] string? list = null,
        [Description("Due date in the user's time zone, formatted yyyy-MM-dd.")] string? due_date = null,
        [Description("Reminder in the user's local time, formatted yyyy-MM-ddTHH:mm.")] string? reminder = null,
        [Description("\"low\", \"normal\" or \"high\". Default normal.")] string? importance = null,
        [Description("Plain-text notes.")] string? notes = null,
        CancellationToken ct = default) => await Run(async () =>
    {
        GraphTaskList target = await this.ResolveListAsync(list, ct);
        Dictionary<string, object> body = new Dictionary<string, object> { ["title"] = title };
        if (due_date is not null)
        {
            body["dueDateTime"] = clock.DueDate(TodoClock.ParseDate(due_date, nameof(due_date)));
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
        return TaskViews.ToView(created, target, clock);
    });

    [McpServerTool(Name = "update_task", Idempotent = true)]
    [Description("""
        Changes a task. Only the fields you pass are changed. Use clear_due_date / clear_reminder to
        remove them, and status to reopen a completed task.
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
        CancellationToken ct = default) => await Run(async () =>
    {
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

        if (patch.Count == 0)
        {
            throw new ArgumentException("Nothing to change: pass at least one field.");
        }

        GraphTaskList list = await this.ResolveListAsync(list_id, ct);
        GraphTask updated = await graph.UpdateTaskAsync(list.Id, task_id, patch, ct);
        return TaskViews.ToView(updated, list, clock);
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

    // ---- helpers ---------------------------------------------------------------------------

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
