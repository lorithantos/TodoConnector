namespace TodoMcp.Tests;

/// <summary>delete_task, delete_task_list and rename_task_list: the Graph call each makes, and the built-in lists they refuse.</summary>
public class DeleteAndRenameTests(McpEndToEndTests.Factory factory) : IClassFixture<McpEndToEndTests.Factory>
{
    private readonly ToolClient tools = new ToolClient(factory);

    [Fact]
    public async Task Delete_task_sends_a_DELETE_for_the_task_and_returns_what_it_deleted()
    {
        int mark = this.tools.Mark();
        JsonElement view = ToolClient.Payload(await this.tools.Call("delete_task", new { list_id = "L1", task_id = "T1" }));

        Assert.Contains(this.tools.Since(mark), r => r.Method == "DELETE" && r.Path == "/v1.0/me/todo/lists/L1/tasks/T1" && r.Body is null);
        Assert.Equal("File taxes", view.GetProperty("title").GetString());
        Assert.Equal("Tasks", view.GetProperty("listName").GetString());
    }

    [Fact]
    public async Task Delete_task_list_deletes_a_users_list_and_counts_its_open_tasks()
    {
        int mark = this.tools.Mark();
        JsonElement view = ToolClient.Payload(await this.tools.Call("delete_task_list", new { list = "work" }));

        Assert.Contains(this.tools.Since(mark), r => r.Method == "DELETE" && r.Path == "/v1.0/me/todo/lists/L4");
        Assert.Equal("Work", view.GetProperty("name").GetString());
        Assert.Equal(2, view.GetProperty("openTasks").GetInt32());
    }

    [Theory]
    [InlineData("Tasks", "defaultList")]
    [InlineData("Flagged email", "flaggedEmails")]
    public async Task Delete_task_list_refuses_a_built_in_list_without_deleting_anything(string list, string wellKnown)
    {
        int mark = this.tools.Mark();
        JsonElement result = await this.tools.Call("delete_task_list", new { list });

        Assert.True(ToolClient.IsError(result), result.ToString());
        Assert.Contains($"'{list}' is a built-in To Do list ({wellKnown}) and cannot be deleted.", ToolClient.Text(result));
        Assert.DoesNotContain(this.tools.Since(mark), r => r.Method != "GET");
    }

    [Fact]
    public async Task Rename_task_list_patches_the_display_name()
    {
        int mark = this.tools.Mark();
        JsonElement view = ToolClient.Payload(await this.tools.Call("rename_task_list", new { list = "Work", new_name = " Office " }));

        GraphRequest patch = Assert.Single(this.tools.Since(mark), r => r.Method == "PATCH");
        Assert.Equal("/v1.0/me/todo/lists/L4", patch.Path);
        Assert.Equal("Office", ToolClient.Body(patch).GetProperty("displayName").GetString());
        Assert.Equal("Office", view.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("Tasks")]
    [InlineData("Flagged email")]
    public async Task Rename_task_list_refuses_a_built_in_list_without_patching(string list)
    {
        int mark = this.tools.Mark();
        JsonElement result = await this.tools.Call("rename_task_list", new { list, new_name = "Mine" });

        Assert.True(ToolClient.IsError(result), result.ToString());
        Assert.Contains("cannot be renamed.", ToolClient.Text(result));
        Assert.DoesNotContain(this.tools.Since(mark), r => r.Method != "GET");
    }
}
