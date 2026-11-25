namespace TodoMcp.Tests;

/// <summary>Steps are Graph checklistItems under a task: what each step tool sends, and that it hands back the task's steps.</summary>
public class StepTests(McpEndToEndTests.Factory factory) : IClassFixture<McpEndToEndTests.Factory>
{
    private const string Steps = "/v1.0/me/todo/lists/L1/tasks/T1/checklistItems";

    private readonly ToolClient tools = new ToolClient(factory);

    [Fact]
    public async Task Get_task_returns_the_task_with_its_steps()
    {
        JsonElement view = ToolClient.Payload(await this.tools.Call("get_task", new { list_id = "L1", task_id = "T1" }));

        JsonElement step = Assert.Single(view.GetProperty("steps").EnumerateArray());
        Assert.Equal("S1", step.GetProperty("id").GetString());
        Assert.Equal("Gather receipts", step.GetProperty("text").GetString());
        Assert.True(step.GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task Add_step_posts_the_text_and_returns_the_tasks_steps()
    {
        int mark = this.tools.Mark();
        JsonElement view = ToolClient.Payload(await this.tools.Call("add_step", new { list_id = "L1", task_id = "T1", text = "Call accountant" }));

        GraphRequest post = Assert.Single(this.tools.Since(mark), r => r.Method == "POST");
        Assert.Equal(Steps, post.Path);
        Assert.Equal("""{"displayName":"Call accountant"}""", post.Body);
        Assert.Equal("File taxes", view.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Array, view.GetProperty("steps").ValueKind);
    }

    [Fact]
    public async Task Update_step_ticks_a_step_off_without_touching_its_text()
    {
        int mark = this.tools.Mark();
        ToolClient.Payload(await this.tools.Call("update_step", new { list_id = "L1", task_id = "T1", step_id = "S1", done = true }));

        GraphRequest patch = Assert.Single(this.tools.Since(mark), r => r.Method == "PATCH");
        Assert.Equal(Steps + "/S1", patch.Path);
        Assert.Equal("""{"isChecked":true}""", patch.Body);
    }

    [Fact]
    public async Task Update_step_renames_a_step_without_touching_its_tick()
    {
        int mark = this.tools.Mark();
        ToolClient.Payload(await this.tools.Call("update_step", new { list_id = "L1", task_id = "T1", step_id = "S1", text = "Gather all receipts" }));

        GraphRequest patch = Assert.Single(this.tools.Since(mark), r => r.Method == "PATCH");
        Assert.Equal("""{"displayName":"Gather all receipts"}""", patch.Body);
    }

    [Fact]
    public async Task Update_step_with_nothing_to_change_is_refused()
    {
        JsonElement result = await this.tools.Call("update_step", new { list_id = "L1", task_id = "T1", step_id = "S1" });

        Assert.True(ToolClient.IsError(result), result.ToString());
        Assert.Contains("Nothing to change: pass text or done.", ToolClient.Text(result));
    }

    [Fact]
    public async Task Delete_step_sends_a_DELETE_for_the_step()
    {
        int mark = this.tools.Mark();
        ToolClient.Payload(await this.tools.Call("delete_step", new { list_id = "L1", task_id = "T1", step_id = "S1" }));

        GraphRequest delete = Assert.Single(this.tools.Since(mark), r => r.Method == "DELETE");
        Assert.Equal(Steps + "/S1", delete.Path);
    }
}
