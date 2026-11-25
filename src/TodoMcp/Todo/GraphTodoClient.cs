using System.Net.Http.Headers;
using Microsoft.Identity.Abstractions;

namespace TodoMcp.Todo;

/// <summary>Calls the Graph To Do API as the signed-in user (token from the on-behalf-of flow).</summary>
public sealed class GraphTodoClient(
    HttpClient http,
    IAuthorizationHeaderProvider authHeaders,
    IHttpContextAccessor httpContext)
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/Tasks.ReadWrite"];

    // Nulls inside a PATCH dictionary are kept on purpose: they clear a field in Graph.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<List<GraphTaskList>> GetListsAsync(CancellationToken ct) =>
        await this.GetAllAsync<GraphTaskList>("me/todo/lists", ct);

    public async Task<List<GraphTask>> GetTasksAsync(string listId, bool includeCompleted, int max, CancellationToken ct)
    {
        string url = $"me/todo/lists/{Uri.EscapeDataString(listId)}/tasks?$top={Math.Clamp(max, 1, 100)}";
        if (!includeCompleted)
        {
            url += "&$filter=" + Uri.EscapeDataString("status ne 'completed'");
        }

        return await this.GetAllAsync<GraphTask>(url, ct, max);
    }

    public Task<GraphTask> CreateTaskAsync(string listId, object body, CancellationToken ct) =>
        this.SendAsync<GraphTask>(HttpMethod.Post, $"me/todo/lists/{Uri.EscapeDataString(listId)}/tasks", body, ct);

    public Task<GraphTask> UpdateTaskAsync(string listId, string taskId, object patch, CancellationToken ct) =>
        this.SendAsync<GraphTask>(HttpMethod.Patch,
            $"me/todo/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}", patch, ct);

    public Task<GraphTaskList> CreateListAsync(string name, CancellationToken ct) =>
        this.SendAsync<GraphTaskList>(HttpMethod.Post, "me/todo/lists", new { displayName = name }, ct);

    private async Task<List<T>> GetAllAsync<T>(string url, CancellationToken ct, int max = int.MaxValue)
    {
        List<T> results = new List<T>();
        string? next = url;
        while (next is not null && results.Count < max)
        {
            GraphPage<T> page = await this.SendAsync<GraphPage<T>>(HttpMethod.Get, next, null, ct);
            results.AddRange(page.Value);
            next = page.NextLink;
        }
        return results.Count > max ? results[..max] : results;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using HttpRequestMessage request = new HttpRequestMessage(method, url);
        ClaimsPrincipal user = httpContext.HttpContext?.User
            ?? throw new InvalidOperationException("No signed-in user on this request.");
        string header = await authHeaders.CreateAuthorizationHeaderForUserAsync(Scopes, claimsPrincipal: user, cancellationToken: ct);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(header);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            throw new GraphRequestException((int)response.StatusCode, ExtractMessage(text));
        }
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
        }
        catch { return body.Length > 300 ? body[..300] : body; }
    }
}

public sealed class GraphRequestException(int status, string message)
    : Exception($"Microsoft Graph returned {status}: {message}")
{
    public int Status { get; } = status;
}
