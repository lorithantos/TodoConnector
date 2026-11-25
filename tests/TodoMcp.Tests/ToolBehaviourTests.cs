namespace TodoMcp.Tests;

/// <summary>
/// What the tools send to Graph and what they hand back on failure, through the real MCP
/// endpoint with McpEndToEndTests' fake user, fake Graph and fixed clock. The fake Graph answers
/// PATCH on L1/T1 and 404s anything it does not know, which is the Graph error used below.
/// </summary>
public class ToolBehaviourTests(McpEndToEndTests.Factory factory) : IClassFixture<McpEndToEndTests.Factory>
{
    private async Task<JsonElement> CallTool(string name, object arguments)
    {
        HttpClient client = factory.CreateClient();
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");

        HttpResponseMessage response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        string json = text.TrimStart().StartsWith('{')
            ? text
            : text.Split('\n').Last(l => l.StartsWith("data:"))["data:".Length..];
        return JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();
    }

    private static bool IsError(JsonElement result) =>
        result.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean();

    private static string? Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString();

    private JsonElement LastPatchBody()
    {
        GraphRequest patch = factory.Graph.Requests.Last(r => r.Method == "PATCH");
        return JsonDocument.Parse(patch.Body!).RootElement.Clone();
    }

    [Fact]
    public async Task Complete_task_patches_the_status_to_completed()
    {
        JsonElement result = await this.CallTool("complete_task", new { list_id = "L1", task_id = "T1" });
        Assert.False(IsError(result), result.ToString());

        Assert.Equal("completed", this.LastPatchBody().GetProperty("status").GetString());
    }

    [Fact]
    public async Task Clear_reminder_sends_a_null_reminder_time_and_turns_the_reminder_off()
    {
        JsonElement result = await this.CallTool("update_task", new { list_id = "L1", task_id = "T1", clear_reminder = true });
        Assert.False(IsError(result), result.ToString());

        JsonElement body = this.LastPatchBody();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reminderDateTime").ValueKind);
        Assert.False(body.GetProperty("isReminderOn").GetBoolean());
    }

    [Fact]
    public async Task A_graph_error_comes_back_as_a_readable_tool_error()
    {
        JsonElement result = await this.CallTool("complete_task", new { list_id = "L1", task_id = "no-such-task" });

        Assert.True(IsError(result), result.ToString());
        Assert.Contains("Microsoft Graph returned 404", Text(result));
    }

    [Fact]
    public async Task Create_task_rejects_an_unknown_importance_with_a_readable_tool_error()
    {
        JsonElement result = await this.CallTool("create_task", new { title = "x", importance = "urgent" });

        Assert.True(IsError(result), result.ToString());
        Assert.Contains("importance must be low, normal or high.", Text(result));
    }

    [Fact]
    public async Task Update_task_rejects_an_unknown_importance_with_a_readable_tool_error()
    {
        JsonElement result = await this.CallTool("update_task", new { list_id = "L1", task_id = "T1", importance = "urgent" });

        Assert.True(IsError(result), result.ToString());
        Assert.Contains("importance must be low, normal or high.", Text(result));
    }
}
