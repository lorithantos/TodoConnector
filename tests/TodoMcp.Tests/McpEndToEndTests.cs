using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;
using TodoMcp.Todo;

namespace TodoMcp.Tests;

/// <summary>
/// Drives the real MCP endpoint with a fake signed-in user and a fake Graph, to check the
/// tool wiring end to end without Entra or Microsoft Graph.
/// </summary>
public class McpEndToEndTests(McpEndToEndTests.Factory factory) : IClassFixture<McpEndToEndTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeGraph Graph { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AzureAd:ClientId", "11111111-2222-3333-4444-555555555555");
            builder.UseSetting("AzureAd:ClientSecret", "test-secret");
            builder.UseSetting("TodoMcp:AllowedUsers:0", "me@example.com");
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                });
                services.AddSingleton<IAuthorizationHeaderProvider, FakeHeaders>();
                services.AddHttpClient<GraphTodoClient>().ConfigurePrimaryHttpMessageHandler(() => Graph);
            });
        }
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
        : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("preferred_username", "me@example.com")], "Test")), "Test")));
    }

    private sealed class FakeHeaders : IAuthorizationHeaderProvider
    {
        public Task<string> CreateAuthorizationHeaderForUserAsync(IEnumerable<string> s, AuthorizationHeaderProviderOptions? o = null, ClaimsPrincipal? c = null, CancellationToken ct = default) => Task.FromResult("Bearer fake");
        public Task<string> CreateAuthorizationHeaderForAppAsync(string s, AuthorizationHeaderProviderOptions? o = null, CancellationToken ct = default) => Task.FromResult("Bearer fake");
        public Task<string> CreateAuthorizationHeaderAsync(IEnumerable<string> s, AuthorizationHeaderProviderOptions? o = null, ClaimsPrincipal? c = null, CancellationToken ct = default) => Task.FromResult("Bearer fake");
    }

    public sealed class FakeGraph : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.PathAndQuery;
            lock (Requests) Requests.Add((request.Method.Method, path, body));

            string json = (request.Method.Method, request.RequestUri.AbsolutePath) switch
            {
                ("GET", "/v1.0/me/todo/lists") =>
                    """{"value":[{"id":"L1","displayName":"Tasks","wellknownListName":"defaultList"},{"id":"L2","displayName":"Groceries","wellknownListName":"none"}]}""",
                ("GET", "/v1.0/me/todo/lists/L1/tasks") =>
                    """{"value":[{"id":"T1","title":"File taxes","status":"notStarted","importance":"high","dueDateTime":{"dateTime":"2020-01-01T08:00:00.0000000","timeZone":"UTC"}}]}""",
                ("GET", "/v1.0/me/todo/lists/L2/tasks") => """{"value":[]}""",
                ("POST", "/v1.0/me/todo/lists/L2/tasks") =>
                    """{"id":"T9","title":"Milk","status":"notStarted","importance":"normal"}""",
                ("PATCH", "/v1.0/me/todo/lists/L1/tasks/T1") =>
                    """{"id":"T1","title":"File taxes","status":"completed","importance":"high"}""",
                _ => """{"error":{"message":"unexpected"}}""",
            };
            return new HttpResponseMessage(json.Contains("unexpected") ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private async Task<JsonElement> Rpc(string method, object @params)
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        var json = text.TrimStart().StartsWith('{')
            ? text
            : text.Split('\n').Last(l => l.StartsWith("data:"))["data:".Length..];
        return JsonDocument.Parse(json).RootElement.GetProperty("result").Clone();
    }

    [Fact]
    public async Task Lists_all_tools()
    {
        var result = await Rpc("tools/list", new { });
        var names = result.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Equal(["get_agenda", "list_task_lists", "list_tasks", "create_task", "update_task", "complete_task", "create_task_list"], names);
    }

    [Fact]
    public async Task Agenda_reports_overdue_tasks_across_lists()
    {
        var result = await Rpc("tools/call", new { name = "get_agenda", arguments = new { } });
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), result.ToString());
        Assert.Contains("File taxes", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Contains(factory.Graph.Requests, r => r.Path.Contains("/lists/L2/tasks") && r.Path.Contains("status%20ne%20%27completed%27"));
    }

    [Fact]
    public async Task Create_task_resolves_list_by_name_and_sends_due_date_and_reminder()
    {
        var result = await Rpc("tools/call", new
        {
            name = "create_task",
            arguments = new { title = "Milk", list = "groceries", due_date = "2026-11-26", reminder = "2026-11-26T08:30" },
        });
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), result.ToString());

        var post = factory.Graph.Requests.Last(r => r.Method == "POST");
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal("2026-11-26T00:00:00", body.RootElement.GetProperty("dueDateTime").GetProperty("dateTime").GetString());
        Assert.Equal("2026-11-26T08:30:00", body.RootElement.GetProperty("reminderDateTime").GetProperty("dateTime").GetString());
        Assert.True(body.RootElement.GetProperty("isReminderOn").GetBoolean());
    }

    [Fact]
    public async Task Bad_dates_come_back_as_a_readable_tool_error()
    {
        var result = await Rpc("tools/call", new { name = "create_task", arguments = new { title = "x", due_date = "next friday" } });
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("2026-11-25", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Update_can_clear_the_due_date_with_an_explicit_null()
    {
        var result = await Rpc("tools/call", new
        {
            name = "update_task",
            arguments = new { list_id = "L1", task_id = "T1", clear_due_date = true },
        });
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), result.ToString());
        var patch = factory.Graph.Requests.Last(r => r.Method == "PATCH");
        Assert.Contains("\"dueDateTime\":null", patch.Body);
    }
}
