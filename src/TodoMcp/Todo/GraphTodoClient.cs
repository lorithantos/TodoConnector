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

    public Task<GraphTask> GetTaskAsync(string listId, string taskId, CancellationToken ct) =>
        this.SendAsync<GraphTask>(HttpMethod.Get, TaskUrl(listId, taskId), null, ct);

    /// <summary>https://learn.microsoft.com/graph/api/todotask-delete?view=graph-rest-1.0 (204, no body).</summary>
    public Task DeleteTaskAsync(string listId, string taskId, CancellationToken ct) =>
        this.SendNoContentAsync(HttpMethod.Delete, TaskUrl(listId, taskId), ct);

    /// <summary>https://learn.microsoft.com/graph/api/todotasklist-update?view=graph-rest-1.0</summary>
    public Task<GraphTaskList> RenameListAsync(string listId, string name, CancellationToken ct) =>
        this.SendAsync<GraphTaskList>(HttpMethod.Patch, ListUrl(listId), new { displayName = name }, ct);

    /// <summary>https://learn.microsoft.com/graph/api/todotasklist-delete?view=graph-rest-1.0 (204, no body).</summary>
    public Task DeleteListAsync(string listId, CancellationToken ct) =>
        this.SendNoContentAsync(HttpMethod.Delete, ListUrl(listId), ct);

    /// <summary>https://learn.microsoft.com/graph/api/todotask-list-checklistitems?view=graph-rest-1.0</summary>
    public async Task<List<GraphChecklistItem>> GetStepsAsync(string listId, string taskId, CancellationToken ct) =>
        await this.GetAllAsync<GraphChecklistItem>(TaskUrl(listId, taskId) + "/checklistItems", ct);

    /// <summary>https://learn.microsoft.com/graph/api/todotask-post-checklistitems?view=graph-rest-1.0</summary>
    public Task<GraphChecklistItem> CreateStepAsync(string listId, string taskId, object body, CancellationToken ct) =>
        this.SendAsync<GraphChecklistItem>(HttpMethod.Post, TaskUrl(listId, taskId) + "/checklistItems", body, ct);

    /// <summary>https://learn.microsoft.com/graph/api/checklistitem-update?view=graph-rest-1.0</summary>
    public Task<GraphChecklistItem> UpdateStepAsync(string listId, string taskId, string stepId, object patch, CancellationToken ct) =>
        this.SendAsync<GraphChecklistItem>(HttpMethod.Patch, StepUrl(listId, taskId, stepId), patch, ct);

    /// <summary>https://learn.microsoft.com/graph/api/checklistitem-delete?view=graph-rest-1.0 (204, no body).</summary>
    public Task DeleteStepAsync(string listId, string taskId, string stepId, CancellationToken ct) =>
        this.SendNoContentAsync(HttpMethod.Delete, StepUrl(listId, taskId, stepId), ct);

    private static string ListUrl(string listId) => $"me/todo/lists/{Uri.EscapeDataString(listId)}";

    private static string TaskUrl(string listId, string taskId) => $"{ListUrl(listId)}/tasks/{Uri.EscapeDataString(taskId)}";

    private static string StepUrl(string listId, string taskId, string stepId) =>
        $"{TaskUrl(listId, taskId)}/checklistItems/{Uri.EscapeDataString(stepId)}";

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
        using HttpResponseMessage response = await this.SendCheckedAsync(method, url, body, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    /// <summary>For DELETE, which Graph answers 204 with no body to read.</summary>
    private async Task SendNoContentAsync(HttpMethod method, string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await this.SendCheckedAsync(method, url, null, ct);
    }

    private async Task<HttpResponseMessage> SendCheckedAsync(HttpMethod method, string url, object? body, CancellationToken ct)
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

        HttpResponseMessage response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                string text = await response.Content.ReadAsStringAsync(ct);
                throw new GraphRequestException((int)response.StatusCode, ExtractMessage(text));
            }
        }
        return response;
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
