using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace TodoMcp.Tests;

/// <summary>
/// GET /oauth/callback: Entra's answer arrives at the server, is logged, and is forwarded to the
/// redirect_uri Claude gave /oauth/authorize, with Claude's own state. A state the server cannot
/// trust sends the browser nowhere. Each test drives the real pipeline and reads the Location
/// header Claude's browser would follow.
/// </summary>
public class FacadeCallbackTests(FacadeTests.Factory factory) : IClassFixture<FacadeTests.Factory>
{
    private const string Callback = "https://claude.ai/api/mcp/auth_callback";

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Starts a sign-in as Claude would and returns the state the server handed Entra.</summary>
    private async Task<string> EntraStateFor(string claudeState)
    {
        string url = QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = OAuthFacade.PublicClientId,
            ["redirect_uri"] = Callback,
            ["state"] = claudeState,
            ["code_challenge"] = "xyz",
            ["code_challenge_method"] = "S256",
        });
        HttpResponseMessage response = await this.Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();
    }

    /// <summary>A state protected with the app's key ring, as if issued at <paramref name="issuedAt"/>.</summary>
    private string StateIssuedAt(DateTimeOffset issuedAt, string redirectUri, string claudeState) =>
        new OAuthStateProtector(factory.Services.GetRequiredService<IDataProtectionProvider>(), new FixedTime(issuedAt))
            .Protect(redirectUri, claudeState);

    private Task<HttpResponseMessage> CallbackWith(Dictionary<string, string?> query) =>
        this.Client().GetAsync(QueryHelpers.AddQueryString(OAuthFacade.CallbackPath, query));

    private static Dictionary<string, StringValues> Forwarded(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri location = response.Headers.Location!;
        Assert.StartsWith(Callback + "?", location.ToString());
        return QueryHelpers.ParseQuery(location.Query);
    }

    private void AssertRefused(HttpResponseMessage response, string reasonFragment)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains(factory.Logs.Warnings(), l => l.Message.Contains("OAuth callback refused") && l.Message.Contains(reasonFragment));
    }

    [Fact]
    public async Task A_code_is_forwarded_to_claude_with_claudes_original_state_and_is_never_logged()
    {
        factory.Logs.Lines.Clear();
        string state = await this.EntraStateFor("claude-state-1");

        HttpResponseMessage response = await this.CallbackWith(new Dictionary<string, string?>
        {
            ["code"] = "entra-code-7f3a",
            ["state"] = state,
            ["session_state"] = "ignored",
        });

        Dictionary<string, StringValues> q = Forwarded(response);
        Assert.Equal("entra-code-7f3a", q["code"]);
        Assert.Equal("claude-state-1", q["state"]);
        Assert.False(q.ContainsKey("error"));
        Assert.Contains(factory.Logs.Lines, l => l.Level == LogLevel.Information && l.Message.Contains("authorization code"));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Message.Contains("entra-code-7f3a"));
    }

    [Fact]
    public async Task An_entra_error_is_forwarded_to_claude_and_logged_as_a_warning_with_its_description()
    {
        factory.Logs.Lines.Clear();
        string state = await this.EntraStateFor("claude-state-2");
        const string Description = "AADSTS50020: User account from identity provider does not exist in tenant.";

        HttpResponseMessage response = await this.CallbackWith(new Dictionary<string, string?>
        {
            ["error"] = "access_denied",
            ["error_description"] = Description,
            ["state"] = state,
        });

        Dictionary<string, StringValues> q = Forwarded(response);
        Assert.Equal("access_denied", q["error"]);
        Assert.Equal(Description, q["error_description"]);
        Assert.Equal("claude-state-2", q["state"]);
        Assert.False(q.ContainsKey("code"));
        LogLine line = Assert.Single(factory.Logs.Warnings(), l => l.Message.Contains("AADSTS50020"));
        Assert.Contains("access_denied", line.Message);
    }

    [Fact]
    public async Task A_response_with_neither_code_nor_error_is_forwarded_as_a_server_error()
    {
        string state = await this.EntraStateFor("claude-state-7");

        HttpResponseMessage response = await this.CallbackWith(new Dictionary<string, string?> { ["state"] = state });

        Dictionary<string, StringValues> q = Forwarded(response);
        Assert.Equal("server_error", q["error"]);
        Assert.Equal("claude-state-7", q["state"]);
    }

    [Theory]
    [InlineData("flip")]
    [InlineData("garbage")]
    [InlineData("missing")]
    public async Task A_state_the_server_did_not_issue_is_refused_and_sends_the_browser_nowhere(string kind)
    {
        factory.Logs.Lines.Clear();
        string state = await this.EntraStateFor("claude-state-3");
        int middle = state.Length / 2;
        string? sent = kind switch
        {
            "flip" => state[..middle] + (state[middle] == 'A' ? 'B' : 'A') + state[(middle + 1)..],
            "garbage" => "not-a-state",
            _ => null,
        };

        HttpResponseMessage response = await this.CallbackWith(new Dictionary<string, string?> { ["code"] = "c", ["state"] = sent });

        this.AssertRefused(response, kind == "missing" ? "missing" : "did not unprotect");
    }

    [Fact]
    public async Task A_state_older_than_ten_minutes_is_refused()
    {
        factory.Logs.Lines.Clear();
        string state = this.StateIssuedAt(FacadeTests.Factory.Now - TimeSpan.FromMinutes(11), Callback, "claude-state-4");

        HttpResponseMessage response = await this.CallbackWith(new Dictionary<string, string?> { ["code"] = "c", ["state"] = state });

        this.AssertRefused(response, "expired");
    }

    [Fact]
    public async Task A_state_nine_minutes_old_is_still_accepted()
    {
        string state = this.StateIssuedAt(FacadeTests.Factory.Now - TimeSpan.FromMinutes(9), Callback, "claude-state-5");

        HttpResponseMessage response = await this.CallbackWith(new Dictionary<string, string?> { ["code"] = "c", ["state"] = state });

        Assert.Equal("claude-state-5", Forwarded(response)["state"]);
    }

    [Fact]
    public async Task A_state_naming_a_redirect_off_the_allow_list_is_refused()
    {
        factory.Logs.Lines.Clear();
        string state = this.StateIssuedAt(FacadeTests.Factory.Now, "https://evil.example/cb", "claude-state-6");

        HttpResponseMessage response = await this.CallbackWith(new Dictionary<string, string?> { ["code"] = "c", ["state"] = state });

        this.AssertRefused(response, "https://evil.example/cb");
    }
}
