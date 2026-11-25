namespace TodoMcp.Tests;

/// <summary>
/// The facade's refusals: each request below is valid in every respect but one, so a 400 can
/// only come from the check under test, and its message says which check that was.
/// </summary>
public class FacadeGuardTests(FacadeTests.Factory factory) : IClassFixture<FacadeTests.Factory>
{
    private const string Callback = "https://claude.ai/api/mcp/auth_callback";

    private static readonly EntraSettings Entra = new EntraSettings("https://login.microsoftonline.com/", "common", "cid");

    private static readonly IReadOnlyDictionary<string, string> Secret = new Dictionary<string, string> { ["client_secret"] = "secret" };

    private async Task<HttpResponseMessage> Authorize(string query)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await client.GetAsync($"/oauth/authorize?redirect_uri={Uri.EscapeDataString(Callback)}&state=abc&{query}");
    }

    [Theory]
    [InlineData("response_type=code&code_challenge_method=S256")]
    [InlineData("response_type=code&code_challenge=xyz&code_challenge_method=plain")]
    [InlineData("response_type=code&code_challenge=xyz")]
    public async Task Authorize_requires_pkce_with_s256(string query)
    {
        HttpResponseMessage response = await this.Authorize(query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PKCE with S256 is required.", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("response_type=token&code_challenge=xyz&code_challenge_method=S256")]
    [InlineData("code_challenge=xyz&code_challenge_method=S256")]
    public async Task Authorize_accepts_only_the_code_response_type(string query)
    {
        HttpResponseMessage response = await this.Authorize(query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Only response_type=code is supported.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Token_form_rejects_a_code_exchange_with_a_redirect_off_the_allow_list()
    {
        Dictionary<string, string>? form = OAuthFacade.BuildEntraTokenForm(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "c",
            ["code_verifier"] = "v",
            ["redirect_uri"] = "https://evil.example/cb",
        }, Entra, Secret, new TodoMcpOptions(), out string? error);

        Assert.Null(form);
        Assert.Contains("redirect_uri", error);
    }

    [Fact]
    public void Token_form_relays_the_refresh_token_with_the_secret()
    {
        Dictionary<string, string>? form = OAuthFacade.BuildEntraTokenForm(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "r1",
            ["client_id"] = OAuthFacade.PublicClientId,
        }, Entra, Secret, new TodoMcpOptions(), out string? error);

        Assert.Null(error);
        Assert.Equal("refresh_token", form!["grant_type"]);
        Assert.Equal("r1", form["refresh_token"]);
        Assert.Equal("cid", form["client_id"]);
        Assert.Equal("secret", form["client_secret"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Token_form_rejects_a_refresh_without_a_refresh_token(string? refreshToken)
    {
        Dictionary<string, string> incoming = new Dictionary<string, string> { ["grant_type"] = "refresh_token" };
        if (refreshToken is not null)
        {
            incoming["refresh_token"] = refreshToken;
        }

        Dictionary<string, string>? form = OAuthFacade.BuildEntraTokenForm(incoming, Entra, Secret, new TodoMcpOptions(), out string? error);

        Assert.Null(form);
        Assert.Equal("refresh_token is required.", error);
    }
}
