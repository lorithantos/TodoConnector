using Microsoft.AspNetCore.Http;

namespace TodoMcp.Tests;

/// <summary>
/// GraphTodoClient's paging, driven directly against a fake Graph that serves fixed pages, so a
/// test can say exactly which pages exist and count the requests that fetched them.
/// </summary>
public class GraphPagingTests
{
    private sealed class PagedGraph(IReadOnlyDictionary<string, string> pages) : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Requested { get; } = new System.Collections.Concurrent.ConcurrentQueue<string>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            this.Requested.Enqueue(path);
            bool found = pages.TryGetValue(path, out string? json);
            return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new StringContent(found ? json! : """{"error":{"message":"no such page"}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeHeaders : Microsoft.Identity.Abstractions.IAuthorizationHeaderProvider
    {
        public Task<string> CreateAuthorizationHeaderForUserAsync(IEnumerable<string> s, Microsoft.Identity.Abstractions.AuthorizationHeaderProviderOptions? o = null, ClaimsPrincipal? c = null, CancellationToken ct = default) => Task.FromResult("Bearer fake");
        public Task<string> CreateAuthorizationHeaderForAppAsync(string s, Microsoft.Identity.Abstractions.AuthorizationHeaderProviderOptions? o = null, CancellationToken ct = default) => Task.FromResult("Bearer fake");
        public Task<string> CreateAuthorizationHeaderAsync(IEnumerable<string> s, Microsoft.Identity.Abstractions.AuthorizationHeaderProviderOptions? o = null, ClaimsPrincipal? c = null, CancellationToken ct = default) => Task.FromResult("Bearer fake");
    }

    private static GraphTodoClient Client(PagedGraph graph)
    {
        HttpClient http = new HttpClient(graph) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        HttpContextAccessor accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "me@example.com")], "Test")),
            },
        };
        return new GraphTodoClient(http, new FakeHeaders(), accessor);
    }

    [Fact]
    public async Task Follows_next_link_until_the_last_page()
    {
        PagedGraph graph = new PagedGraph(new Dictionary<string, string>
        {
            ["/v1.0/me/todo/lists"] =
                """{"value":[{"id":"L1","displayName":"Tasks"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/todo/lists?$skiptoken=p2"}""",
            ["/v1.0/me/todo/lists?$skiptoken=p2"] =
                """{"value":[{"id":"L2","displayName":"Groceries"}]}""",
        });

        List<GraphTaskList> lists = await Client(graph).GetListsAsync(CancellationToken.None);

        Assert.Equal(["L1", "L2"], lists.Select(l => l.Id));
        Assert.Equal(2, graph.Requested.Count);
    }

    [Fact]
    public async Task Stops_paging_once_max_tasks_are_in_hand_and_returns_no_more_than_max()
    {
        PagedGraph graph = new PagedGraph(new Dictionary<string, string>
        {
            ["/v1.0/me/todo/lists/L1/tasks?$top=2"] =
                """{"value":[{"id":"T1","title":"a"},{"id":"T2","title":"b"},{"id":"T3","title":"c"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/todo/lists/L1/tasks?$skiptoken=p2"}""",
            ["/v1.0/me/todo/lists/L1/tasks?$skiptoken=p2"] =
                """{"value":[{"id":"T4","title":"d"}]}""",
        });

        List<GraphTask> tasks = await Client(graph).GetTasksAsync("L1", includeCompleted: true, max: 2, CancellationToken.None);

        Assert.Equal(["T1", "T2"], tasks.Select(t => t.Id));
        Assert.Equal(["/v1.0/me/todo/lists/L1/tasks?$top=2"], graph.Requested);
    }
}
