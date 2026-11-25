using Microsoft.Identity.Client;

namespace TodoMcp.Auth;

/// <summary>Where the token relay gets its client assertion. A seam so tests never reach a managed identity endpoint.</summary>
public interface IClientAssertionSource
{
    /// <summary>A signed assertion that proves this app (entra.ClientId) to Entra's token endpoint.</summary>
    Task<string> GetAssertionAsync(EntraSettings entra, CancellationToken cancellationToken);
}

/// <summary>
/// The assertion from the app's managed identity, through Microsoft.Identity.Web's own
/// ManagedIdentityClientAssertion: the same type Microsoft.Identity.Web builds for the
/// on-behalf-of path from the same ClientCredentials entry. It holds the assertion until it nears
/// expiry, and MSAL caches the managed identity token beneath it, so nothing is cached here.
/// </summary>
public sealed class ManagedIdentityAssertionSource : IClientAssertionSource
{
    private readonly ManagedIdentityClientAssertion? assertion;

    public ManagedIdentityAssertionSource(EntraClientCredential credential, ILogger<ManagedIdentityAssertionSource> logger)
    {
        // Under a client secret this source is registered but never asked.
        this.assertion = credential is EntraManagedIdentityCredential mi
            ? new ManagedIdentityClientAssertion(mi.ManagedIdentityClientId, mi.TokenExchangeUrl, logger)
            : null;
    }

    public Task<string> GetAssertionAsync(EntraSettings entra, CancellationToken cancellationToken)
    {
        if (this.assertion is null)
        {
            throw new InvalidOperationException(
                $"A managed identity assertion was requested, but {EntraClientCredential.SourceTypeSetting} is not SignedAssertionFromManagedIdentity.");
        }

        return this.assertion.GetSignedAssertionAsync(new AssertionRequestOptions
        {
            ClientID = entra.ClientId,
            Authority = entra.AuthorityUrl,
            TokenEndpoint = $"{entra.AuthorityUrl}/oauth2/v2.0/token",
            CancellationToken = cancellationToken,
        });
    }
}
