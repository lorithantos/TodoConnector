using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TodoMcp.Tests;

/// <summary>One request the fake Entra token endpoint received, with its form decoded.</summary>
public sealed record EntraRequest(string Url, IReadOnlyDictionary<string, string> Form);

/// <summary>
/// The app's Entra credential: a managed identity assertion when AzureAd:ClientCredentials:0 says so,
/// otherwise the client secret, otherwise no start. The relay tests drive POST /oauth/token through
/// the real pipeline and read the form a fake Entra received, so they fail if the relay sends the
/// wrong credential, not merely if a builder returns the wrong dictionary.
/// </summary>
public class EntraCredentialTests
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private const string MiClientId = "99999999-8888-7777-6666-555555555555";
    private const string TokenExchangeUrl = "api://AzureADTokenExchange/.default";
    private const string Callback = "https://claude.ai/api/mcp/auth_callback";

    /// <summary>What the deployment sets: the managed identity entry and no secret.</summary>
    private static Dictionary<string, string> ManagedIdentity() => new Dictionary<string, string>
    {
        ["AzureAd:ClientId"] = ClientId,
        ["AzureAd:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
        ["AzureAd:ClientCredentials:0:ManagedIdentityClientId"] = MiClientId,
        ["AzureAd:ClientCredentials:0:TokenExchangeUrl"] = TokenExchangeUrl,
    };

    private static Dictionary<string, string> SecretOnly() => new Dictionary<string, string>
    {
        ["AzureAd:ClientId"] = ClientId,
        ["AzureAd:ClientSecret"] = "test-secret",
    };

    public sealed class CredentialFactory(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        public FakeEntra Entra { get; } = new();

        public FakeAssertions Assertions { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Not Development: user-secrets would bring a developer's own AzureAd:ClientSecret in.
            builder.UseEnvironment("Production");
            foreach (KeyValuePair<string, string> setting in settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IClientAssertionSource>(this.Assertions);
                services.AddHttpClient(OAuthFacade.EntraHttpClient).ConfigurePrimaryHttpMessageHandler(() => this.Entra);
            });
        }
    }

    public sealed class FakeAssertions : IClientAssertionSource
    {
        public const string Assertion = "fake-mi-assertion";

        /// <summary>The client id each assertion was requested for.</summary>
        public ConcurrentQueue<string> RequestedFor { get; } = new();

        public Task<string> GetAssertionAsync(EntraSettings entra, CancellationToken cancellationToken)
        {
            this.RequestedFor.Enqueue(entra.ClientId);
            return Task.FromResult(Assertion);
        }
    }

    public sealed class FakeEntra : HttpMessageHandler
    {
        public ConcurrentQueue<EntraRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Dictionary<string, string> form = QueryHelpers.ParseQuery(body).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            this.Requests.Enqueue(new EntraRequest(request.RequestUri!.ToString(), form));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"at","token_type":"Bearer"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static FormUrlEncodedContent CodeExchange() => new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = "c1",
        ["code_verifier"] = "v1",
        ["redirect_uri"] = Callback,
        ["client_id"] = OAuthFacade.PublicClientId,
    });

    [Fact]
    public async Task Token_relay_under_a_managed_identity_sends_the_assertion_and_no_secret()
    {
        using CredentialFactory factory = new CredentialFactory(ManagedIdentity());
        HttpResponseMessage response = await factory.CreateClient().PostAsync("/oauth/token", CodeExchange());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        EntraRequest sent = Assert.Single(factory.Entra.Requests);
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", sent.Url);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", sent.Form["client_assertion_type"]);
        Assert.Equal(FakeAssertions.Assertion, sent.Form["client_assertion"]);
        Assert.False(sent.Form.ContainsKey("client_secret"));
        Assert.Equal(ClientId, sent.Form["client_id"]);
        Assert.Equal("c1", sent.Form["code"]);
        Assert.Equal(ClientId, Assert.Single(factory.Assertions.RequestedFor));
    }

    [Fact]
    public async Task Token_relay_under_a_secret_sends_the_secret_and_no_assertion()
    {
        using CredentialFactory factory = new CredentialFactory(SecretOnly());
        HttpResponseMessage response = await factory.CreateClient().PostAsync("/oauth/token", CodeExchange());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        EntraRequest sent = Assert.Single(factory.Entra.Requests);
        Assert.Equal("test-secret", sent.Form["client_secret"]);
        Assert.False(sent.Form.ContainsKey("client_assertion"));
        Assert.False(sent.Form.ContainsKey("client_assertion_type"));
        Assert.Empty(factory.Assertions.RequestedFor);
    }

    [Fact]
    public void Startup_fails_naming_both_settings_when_neither_credential_is_set()
    {
        using CredentialFactory factory = new CredentialFactory(new Dictionary<string, string>
        {
            ["AzureAd:ClientId"] = ClientId,
            ["AzureAd:ClientSecret"] = "",
        });

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("AzureAd:ClientCredentials:0:SourceType", e.Message);
        Assert.Contains("AzureAd:ClientSecret", e.Message);
    }

    [Fact]
    public void A_managed_identity_entry_wins_over_a_secret_and_carries_its_settings()
    {
        Dictionary<string, string> both = ManagedIdentity();
        both["AzureAd:ClientSecret"] = "also-set";
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(both!).Build();

        EntraManagedIdentityCredential mi = Assert.IsType<EntraManagedIdentityCredential>(EntraClientCredential.From(config));
        Assert.Equal(MiClientId, mi.ManagedIdentityClientId);
        Assert.Equal(TokenExchangeUrl, mi.TokenExchangeUrl);
    }
}
