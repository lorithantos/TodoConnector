namespace TodoMcp.Tests;

/// <summary>
/// move_task is a copy then a delete, because Graph has no move. What it must carry, and that the
/// original goes only once the copy and its steps exist.
/// </summary>
public class MoveTaskTests(McpEndToEndTests.Factory factory) : IClassFixture<McpEndToEndTests.Factory>
{
    private readonly ToolClient tools = new ToolClient(factory);

    [Fact]
    public async Task Move_carries_every_field_and_step_then_deletes_the_original()
    {
        int mark = this.tools.Mark();
        JsonElement view = ToolClient.Payload(await this.tools.Call("move_task", new { list_id = "L1", task_id = "T5", to_list = "work" }));
        List<GraphRequest> sent = this.tools.Since(mark);

        GraphRequest create = Assert.Single(sent, r => r.Method == "POST" && r.Path == "/v1.0/me/todo/lists/L4/tasks");
        JsonElement body = ToolClient.Body(create);
        Assert.Equal("Plan trip", body.GetProperty("title").GetString());
        Assert.Equal("<p>Bring passports</p>", body.GetProperty("body").GetProperty("content").GetString());
        Assert.Equal("html", body.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("high", body.GetProperty("importance").GetString());
        Assert.Equal("inProgress", body.GetProperty("status").GetString());
        Assert.Equal("2026-12-01T08:00:00.0000000", body.GetProperty("dueDateTime").GetProperty("dateTime").GetString());
        Assert.Equal("UTC", body.GetProperty("dueDateTime").GetProperty("timeZone").GetString());
        Assert.Equal("2026-11-30T17:00:00.0000000", body.GetProperty("reminderDateTime").GetProperty("dateTime").GetString());
        Assert.True(body.GetProperty("isReminderOn").GetBoolean());
        Assert.Equal(["Travel"], body.GetProperty("categories").EnumerateArray().Select(c => c.GetString()));
        // Only the members a weekly pattern and a noEnd range need: Graph's read-back defaults
        // (dayOfMonth 0, endDate 0001-01-01, an empty recurrenceTimeZone) are not echoed.
        Assert.Equal(
            """{"type":"weekly","interval":2,"daysOfWeek":["monday","wednesday"],"firstDayOfWeek":"sunday"}""",
            body.GetProperty("recurrence").GetProperty("pattern").GetRawText());
        Assert.Equal("""{"type":"noEnd","startDate":"2026-12-01"}""", body.GetProperty("recurrence").GetProperty("range").GetRawText());

        List<GraphRequest> steps = sent.Where(r => r.Method == "POST" && r.Path == "/v1.0/me/todo/lists/L4/tasks/T50/checklistItems").ToList();
        Assert.Equal(
            ["Book flights:True", "Pack:False"],
            steps.Select(s => ToolClient.Body(s)).Select(b => $"{b.GetProperty("displayName").GetString()}:{b.GetProperty("isChecked").GetBoolean()}"));

        int deleteAt = sent.FindIndex(r => r.Method == "DELETE" && r.Path == "/v1.0/me/todo/lists/L1/tasks/T5");
        Assert.True(deleteAt > sent.IndexOf(steps[^1]), "the original was deleted before every step was copied");
        Assert.True(deleteAt > sent.IndexOf(create));

        Assert.Equal("T50", view.GetProperty("id").GetString());
        Assert.Equal("Work", view.GetProperty("listName").GetString());
        Assert.Equal(2, view.GetProperty("steps").GetArrayLength());
        Assert.Equal("every 2 weeks on Mon, Wed", view.GetProperty("repeats").GetString());
        Assert.Contains("Moved from 'Tasks'", view.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Move_keeps_the_original_when_a_step_fails_and_says_what_exists()
    {
        int mark = this.tools.Mark();
        JsonElement result = await this.tools.Call("move_task", new { list_id = "L1", task_id = "T5", to_list = "Groceries" });

        Assert.True(ToolClient.IsError(result), result.ToString());
        string text = ToolClient.Text(result);
        Assert.Contains("the copy was created in 'Groceries' as task T9 but its steps failed after 0 of 2", text);
        Assert.Contains("Microsoft Graph returned 404", text);
        Assert.Contains("The original in 'Tasks' (task T5) was kept", text);
        Assert.DoesNotContain(this.tools.Since(mark), r => r.Method == "DELETE");
    }

    [Fact]
    public async Task Move_to_the_same_list_is_refused_before_anything_is_written()
    {
        int mark = this.tools.Mark();
        JsonElement result = await this.tools.Call("move_task", new { list_id = "L1", task_id = "T5", to_list = "tasks" });

        Assert.True(ToolClient.IsError(result), result.ToString());
        Assert.Contains("already in 'Tasks'", ToolClient.Text(result));
        Assert.DoesNotContain(this.tools.Since(mark), r => r.Method != "GET");
    }
}
