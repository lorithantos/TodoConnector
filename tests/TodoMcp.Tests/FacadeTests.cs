using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace TodoMcp.Tests;

public class FacadeTests(FacadeTests.Factory factory) : IClassFixture<FacadeTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AzureAd:ClientId", "11111111-2222-3333-4444-555555555555");
            builder.UseSetting("AzureAd:ClientSecret", "test-secret");
            builder.UseSetting("TodoMcp:PublicBaseUrl", "https://todo.example.net");
        }
    }

    private const string Callback = "https://claude.ai/api/mcp/auth_callback";

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Mcp_without_a_token_is_401_with_a_resource_metadata_pointer()
    {
        HttpResponseMessage response = await this.Client().PostAsync("/mcp", new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=\"https://todo.example.net/.well-known/oauth-protected-resource/mcp\"",
            response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Protected_resource_metadata_points_at_this_server()
    {
        using JsonDocument doc = JsonDocument.Parse(await this.Client().GetStringAsync("/.well-known/oauth-protected-resource/mcp"));
        Assert.Equal("https://todo.example.net/mcp", doc.RootElement.GetProperty("resource").GetString());
        Assert.Equal("https://todo.example.net", doc.RootElement.GetProperty("authorization_servers")[0].GetString());
    }

    [Fact]
    public async Task Authorization_server_metadata_lists_the_facade_endpoints()
    {
        using JsonDocument doc = JsonDocument.Parse(await this.Client().GetStringAsync("/.well-known/oauth-authorization-server"));
        Assert.Equal("https://todo.example.net/oauth/token", doc.RootElement.GetProperty("token_endpoint").GetString());
        Assert.Equal("https://todo.example.net/oauth/register", doc.RootElement.GetProperty("registration_endpoint").GetString());
    }

    [Fact]
    public async Task Register_accepts_claude_callback_only()
    {
        HttpResponseMessage ok = await this.Client().PostAsync("/oauth/register",
            new StringContent($$"""{"redirect_uris":["{{Callback}}"],"client_name":"Claude"}"""));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        HttpResponseMessage bad = await this.Client().PostAsync("/oauth/register",
            new StringContent("""{"redirect_uris":["https://evil.example/cb"]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Authorize_redirects_to_entra_without_the_resource_parameter()
    {
        string url = QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = OAuthFacade.PublicClientId,
            ["redirect_uri"] = Callback,
            ["state"] = "abc",
            ["code_challenge"] = "xyz",
            ["code_challenge_method"] = "S256",
            ["resource"] = "https://todo.example.net/mcp",
            ["scope"] = "todo",
        });
        HttpResponseMessage response = await this.Client().GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri location = response.Headers.Location!;
        Assert.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", location.ToString());
        Dictionary<string, StringValues> q = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("11111111-2222-3333-4444-555555555555", q["client_id"]);
        Assert.Equal("openid offline_access api://11111111-2222-3333-4444-555555555555/.default", q["scope"]);
        Assert.Equal("abc", q["state"]);
        Assert.False(q.ContainsKey("resource"));
    }

    [Fact]
    public async Task Authorize_rejects_unknown_redirects()
    {
        HttpResponseMessage response = await this.Client().GetAsync(
            "/oauth/authorize?response_type=code&redirect_uri=https%3A%2F%2Fevil.example%2Fcb&code_challenge=x&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void Token_form_adds_the_secret_and_drops_resource_and_client_credentials()
    {
        EntraSettings entra = new EntraSettings("https://login.microsoftonline.com/", "common", "cid", "secret");
        Dictionary<string, string>? form = OAuthFacade.BuildEntraTokenForm(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "c",
            ["code_verifier"] = "v",
            ["redirect_uri"] = Callback,
            ["resource"] = "https://todo.example.net/mcp",
            ["client_id"] = OAuthFacade.PublicClientId,
        }, entra, new TodoMcpOptions(), out string? error);

        Assert.Null(error);
        Assert.Equal("cid", form!["client_id"]);
        Assert.Equal("secret", form["client_secret"]);
        Assert.False(form.ContainsKey("resource"));
    }

    [Fact]
    public void Token_form_rejects_other_grant_types()
    {
        EntraSettings entra = new EntraSettings("https://login.microsoftonline.com/", "common", "cid", "secret");
        Assert.Null(OAuthFacade.BuildEntraTokenForm(
            new Dictionary<string, string> { ["grant_type"] = "client_credentials" }, entra, new TodoMcpOptions(), out _));
    }
}
