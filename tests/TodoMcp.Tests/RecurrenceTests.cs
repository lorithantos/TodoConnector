namespace TodoMcp.Tests;

/// <summary>
/// The repeat options on create_task and update_task, as Graph receives them, and the sentence a
/// task's recurrence reads as. The factory's clock is 2026-11-24 in Los Angeles.
/// </summary>
public class RecurrenceTests(McpEndToEndTests.Factory factory) : IClassFixture<McpEndToEndTests.Factory>
{
    private readonly ToolClient tools = new ToolClient(factory);

    private async Task<JsonElement> CreatedBody(object arguments)
    {
        int mark = this.tools.Mark();
        JsonElement result = await this.tools.Call("create_task", arguments);
        Assert.False(ToolClient.IsError(result), result.ToString());
        return ToolClient.Body(Assert.Single(this.tools.Since(mark), r => r.Method == "POST"));
    }

    [Theory]
    [InlineData("daily", 1, null, """{"type":"daily","interval":1}""")]
    [InlineData("daily", 3, null, """{"type":"daily","interval":3}""")]
    [InlineData("weekdays", 1, null, """{"type":"weekly","interval":1,"daysOfWeek":["monday","tuesday","wednesday","thursday","friday"],"firstDayOfWeek":"sunday"}""")]
    [InlineData("weekly", 2, "wed, Mon", """{"type":"weekly","interval":2,"daysOfWeek":["monday","wednesday"],"firstDayOfWeek":"sunday"}""")]
    [InlineData("weekly", 1, null, """{"type":"weekly","interval":1,"daysOfWeek":["thursday"],"firstDayOfWeek":"sunday"}""")]
    [InlineData("Monthly", 3, null, """{"type":"absoluteMonthly","interval":3,"dayOfMonth":26}""")]
    [InlineData("yearly", 1, null, """{"type":"absoluteYearly","interval":1,"dayOfMonth":26,"month":11}""")]
    public async Task Each_repeat_form_maps_to_its_graph_pattern(string repeat, int every, string? on, string pattern)
    {
        // 2026-11-26 is a Thursday: weekly with no days repeats on the due date's weekday.
        JsonElement body = await this.CreatedBody(new { title = "x", list = "Groceries", due_date = "2026-11-26", repeat, repeat_every = every, repeat_on = on });

        Assert.Equal(pattern, body.GetProperty("recurrence").GetProperty("pattern").GetRawText());
        Assert.Equal("""{"type":"noEnd","startDate":"2026-11-26"}""", body.GetProperty("recurrence").GetProperty("range").GetRawText());
        Assert.Equal("2026-11-26T00:00:00", body.GetProperty("dueDateTime").GetProperty("dateTime").GetString());
    }

    [Fact]
    public async Task A_repeating_task_without_a_due_date_is_due_today_and_the_result_says_so()
    {
        int mark = this.tools.Mark();
        JsonElement view = ToolClient.Payload(await this.tools.Call("create_task", new { title = "Water plants", list = "Groceries", repeat = "daily" }));
        JsonElement body = ToolClient.Body(Assert.Single(this.tools.Since(mark), r => r.Method == "POST"));

        Assert.Equal("2026-11-24T00:00:00", body.GetProperty("dueDateTime").GetProperty("dateTime").GetString());
        Assert.Equal("2026-11-24", body.GetProperty("recurrence").GetProperty("range").GetProperty("startDate").GetString());
        Assert.Contains("due today (2026-11-24)", view.GetProperty("note").GetString());
    }

    [Theory]
    [InlineData("hourly", 1, null, "repeat must be daily, weekdays, weekly, monthly or yearly, got 'hourly'.")]
    [InlineData("daily", 0, null, "repeat_every must be 1 or more, got 0.")]
    [InlineData("weekly", 1, "mon,funday", "repeat_on: 'funday' is not a day of the week")]
    [InlineData("daily", 1, "mon", "repeat_on only applies to repeat=weekly.")]
    [InlineData(null, 2, null, "repeat_every and repeat_on need repeat")]
    [InlineData(null, 1, "mon", "repeat_every and repeat_on need repeat")]
    public async Task Bad_repeat_options_are_refused_with_a_readable_error_and_nothing_created(
        string? repeat, int every, string? on, string message)
    {
        int mark = this.tools.Mark();
        JsonElement result = await this.tools.Call("create_task", new { title = "x", list = "Groceries", due_date = "2026-11-26", repeat, repeat_every = every, repeat_on = on });

        Assert.True(ToolClient.IsError(result), result.ToString());
        Assert.Contains(message, ToolClient.Text(result));
        Assert.DoesNotContain(this.tools.Since(mark), r => r.Method == "POST");
    }

    [Fact]
    public async Task Clear_recurrence_sends_a_null_recurrence()
    {
        int mark = this.tools.Mark();
        ToolClient.Payload(await this.tools.Call("update_task", new { list_id = "L1", task_id = "T1", clear_recurrence = true }));

        JsonElement patch = ToolClient.Body(Assert.Single(this.tools.Since(mark), r => r.Method == "PATCH"));
        Assert.Equal(JsonValueKind.Null, patch.GetProperty("recurrence").ValueKind);
    }

    [Fact]
    public async Task Update_repeats_from_the_tasks_own_due_date_when_none_is_passed()
    {
        // T1 is due 2020-01-01T08:00Z, which is midnight 2020-01-01 in Los Angeles.
        int mark = this.tools.Mark();
        ToolClient.Payload(await this.tools.Call("update_task", new { list_id = "L1", task_id = "T1", repeat = "monthly" }));

        JsonElement patch = ToolClient.Body(Assert.Single(this.tools.Since(mark), r => r.Method == "PATCH"));
        Assert.Equal("2020-01-01", patch.GetProperty("recurrence").GetProperty("range").GetProperty("startDate").GetString());
        Assert.Equal(1, patch.GetProperty("recurrence").GetProperty("pattern").GetProperty("dayOfMonth").GetInt32());
        Assert.False(patch.TryGetProperty("dueDateTime", out _), "the task's due date was overwritten");
    }

    [Fact]
    public async Task Update_refuses_repeat_with_clear_due_date()
    {
        JsonElement result = await this.tools.Call("update_task", new { list_id = "L1", task_id = "T1", repeat = "daily", clear_due_date = true });

        Assert.True(ToolClient.IsError(result), result.ToString());
        Assert.Contains("A repeating task needs a due date", ToolClient.Text(result));
    }

    [Theory]
    [InlineData("daily", 1, null, null, null, "every day")]
    [InlineData("daily", 3, null, null, null, "every 3 days")]
    [InlineData("weekly", 1, "monday,tuesday,wednesday,thursday,friday", null, null, "every weekday")]
    [InlineData("weekly", 2, "wednesday,monday", null, null, "every 2 weeks on Mon, Wed")]
    [InlineData("absoluteMonthly", 1, null, 15, null, "every month on day 15")]
    [InlineData("absoluteYearly", 1, null, 25, 11, "every year on Nov 25")]
    public void Recurrence_reads_as_a_short_sentence(string type, int interval, string? days, int? dayOfMonth, int? month, string expected)
    {
        GraphRecurrence recurrence = new GraphRecurrence
        {
            Pattern = new GraphRecurrencePattern
            {
                Type = type, Interval = interval, DaysOfWeek = days?.Split(',').ToList(), DayOfMonth = dayOfMonth, Month = month,
            },
        };

        Assert.Equal(expected, Recurrence.Describe(recurrence));
    }
}
