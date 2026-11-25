using System.Net.Http.Headers;
using System.Text;

namespace TodoMcp.Tests;

/// <summary>
/// Calls one tool through the real MCP endpoint of a McpEndToEndTests.Factory and reads what the
/// fake Graph saw since a mark. Tests in one class run one at a time and share the factory, so a
/// mark taken before a call isolates that call's Graph requests.
/// </summary>
internal sealed class ToolClient(McpEndToEndTests.Factory factory)
{
    public int Mark() => factory.Graph.Requests.Count;

    public List<GraphRequest> Since(int mark) => factory.Graph.Requests.Skip(mark).ToList();

    public async Task<JsonElement> Call(string name, object arguments)
    {
        HttpClient client = factory.CreateClient();
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");

        HttpResponseMessage response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        string json = text.TrimStart().StartsWith('{')
            ? text
            : text.Split('\n').Last(l => l.StartsWith("data:"))["data:".Length..];
        return JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();
    }

    public static bool IsError(JsonElement result) =>
        result.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean();

    public static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    /// <summary>A successful result's payload, parsed.</summary>
    public static JsonElement Payload(JsonElement result)
    {
        Assert.False(IsError(result), result.ToString());
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    public static JsonElement Body(GraphRequest request) => JsonDocument.Parse(request.Body!).RootElement.Clone();
}
