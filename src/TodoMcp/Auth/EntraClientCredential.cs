namespace TodoMcp.Auth;

/// <summary>
/// How this app proves itself at Entra's token endpoint when the facade relays a code or refresh
/// token. Decided once, from the same AzureAd section Microsoft.Identity.Web reads for the
/// on-behalf-of exchange, so the relay and the Graph path cannot use different credentials:
/// AzureAd:ClientCredentials:0 with SourceType SignedAssertionFromManagedIdentity wins, then
/// AzureAd:ClientSecret, and with neither the app does not start.
/// </summary>
public abstract class EntraClientCredential
{
    public const string SourceTypeSetting = "AzureAd:ClientCredentials:0:SourceType";
    public const string ClientSecretSetting = "AzureAd:ClientSecret";

    /// <summary>The form fields that carry this credential to Entra's token endpoint.</summary>
    public abstract Task<IReadOnlyDictionary<string, string>> FormFieldsAsync(
        IClientAssertionSource assertions, EntraSettings entra, CancellationToken cancellationToken);

    public static EntraClientCredential From(IConfiguration config)
    {
        IConfigurationSection azureAd = config.GetSection("AzureAd");

        // Bound to the library's own type, so SourceType parses exactly as Microsoft.Identity.Web parses it.
        // Qualified rather than imported: a using here would make the namespace shared with
        // GraphTodoClient.cs, which the house rules would move into the csproj.
        Microsoft.Identity.Abstractions.CredentialDescription[] credentials =
            azureAd.GetSection("ClientCredentials").Get<Microsoft.Identity.Abstractions.CredentialDescription[]>() ?? [];
        if (credentials.Length > 0 &&
            credentials[0].SourceType == Microsoft.Identity.Abstractions.CredentialSource.SignedAssertionFromManagedIdentity)
        {
            return new EntraManagedIdentityCredential(credentials[0].ManagedIdentityClientId, credentials[0].TokenExchangeUrl);
        }

        string? secret = azureAd["ClientSecret"];
        if (!string.IsNullOrWhiteSpace(secret))
        {
            return new EntraClientSecretCredential(secret);
        }

        throw new InvalidOperationException(
            $"No Entra client credential is configured. Set {SourceTypeSetting} to SignedAssertionFromManagedIdentity " +
            $"(with AzureAd:ClientCredentials:0:ManagedIdentityClientId) or set {ClientSecretSetting}.");
    }
}

/// <summary>Workload identity federation: a token for api://AzureADTokenExchange from a managed identity.</summary>
public sealed class EntraManagedIdentityCredential(string? managedIdentityClientId, string? tokenExchangeUrl) : EntraClientCredential
{
    public const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Null means the system-assigned identity.</summary>
    public string? ManagedIdentityClientId { get; } = managedIdentityClientId;

    /// <summary>Null lets the library pick the audience from the authority's cloud.</summary>
    public string? TokenExchangeUrl { get; } = tokenExchangeUrl;

    public override async Task<IReadOnlyDictionary<string, string>> FormFieldsAsync(
        IClientAssertionSource assertions, EntraSettings entra, CancellationToken cancellationToken)
    {
        string assertion = await assertions.GetAssertionAsync(entra, cancellationToken);
        return new Dictionary<string, string>
        {
            ["client_assertion_type"] = JwtBearerAssertionType,
            ["client_assertion"] = assertion,
        };
    }
}

/// <summary>A client secret: local development and the tests. A class, not a record, so ToString cannot print it.</summary>
public sealed class EntraClientSecretCredential(string secret) : EntraClientCredential
{
    private readonly string secret = secret;

    public override Task<IReadOnlyDictionary<string, string>> FormFieldsAsync(
        IClientAssertionSource assertions, EntraSettings entra, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["client_secret"] = this.secret });
}
