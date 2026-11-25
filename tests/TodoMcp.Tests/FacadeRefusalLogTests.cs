namespace TodoMcp.Tests;

/// <summary>
/// Every facade refusal leaves a Warning saying why, so a failed connection is never silent in the
/// App Service log, and no refusal logs the code or secret it was handed.
/// </summary>
public class FacadeRefusalLogTests(FacadeTests.Factory factory) : IClassFixture<FacadeTests.Factory>
{
    private const string Callback = "https://claude.ai/api/mcp/auth_callback";

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private void AssertWarned(string endpoint, string reasonFragment) =>
        Assert.Contains(factory.Logs.Warnings(), l => l.Message.Contains($"OAuth {endpoint} refused") && l.Message.Contains(reasonFragment));

    [Theory]
    [InlineData("not json", "not JSON")]
    [InlineData("""{"redirect_uris":["https://evil.example/register-cb"]}""", "https://evil.example/register-cb")]
    public async Task A_refused_registration_is_logged(string body, string reasonFragment)
    {
        factory.Logs.Lines.Clear();
        HttpResponseMessage response = await this.Client().PostAsync("/oauth/register", new StringContent(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        this.AssertWarned("register", reasonFragment);
    }

    [Theory]
    [InlineData("https://evil.example/authorize-cb", "response_type=code&code_challenge=x&code_challenge_method=S256", "https://evil.example/authorize-cb")]
    [InlineData(Callback, "response_type=token&code_challenge=x&code_challenge_method=S256", "response_type token")]
    [InlineData(Callback, "response_type=code&code_challenge=x&code_challenge_method=plain", "PKCE")]
    public async Task A_refused_authorize_is_logged(string redirectUri, string query, string reasonFragment)
    {
        factory.Logs.Lines.Clear();
        HttpResponseMessage response = await this.Client().GetAsync($"/oauth/authorize?redirect_uri={Uri.EscapeDataString(redirectUri)}&{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        this.AssertWarned("authorize", reasonFragment);
    }

    [Fact]
    public async Task A_refused_token_request_is_logged_without_its_code()
    {
        factory.Logs.Lines.Clear();
        HttpResponseMessage response = await this.Client().PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "never-log-this-code",
            ["code_verifier"] = "v",
            ["redirect_uri"] = "https://evil.example/token-cb",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        this.AssertWarned("token", "an allowed redirect_uri");
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Message.Contains("never-log-this-code"));
    }

    [Fact]
    public async Task A_token_request_with_an_unsupported_grant_is_logged()
    {
        factory.Logs.Lines.Clear();
        HttpResponseMessage response = await this.Client().PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        this.AssertWarned("token", "client_credentials");
    }
}
