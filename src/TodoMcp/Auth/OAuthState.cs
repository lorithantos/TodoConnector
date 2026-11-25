using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace TodoMcp.Auth;

/// <summary>
/// What the facade needs back when Entra redirects to /oauth/callback: where Claude wants the
/// answer and the state Claude sent. Carried inside the state parameter Entra round-trips, so
/// the facade stores nothing.
/// </summary>
public sealed record FacadeState(string RedirectUri, string ClientState, DateTimeOffset ExpiresAt);

/// <summary>
/// Protects <see cref="FacadeState"/> for its trip through Entra: encrypted and signed with ASP.NET
/// Core Data Protection, so a state that was altered, or protected under another key ring, fails
/// to unprotect. Expiry is clocked by the app's TimeProvider and carried in the payload, not by
/// ITimeLimitedDataProtector, whose Unprotect compares against the system clock and offers no
/// way to supply another.
/// </summary>
public sealed class OAuthStateProtector(IDataProtectionProvider provider, TimeProvider time)
{
    public const string Purpose = "TodoMcp.OAuthFacade.State.v1";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IDataProtector protector = provider.CreateProtector(Purpose);

    public string Protect(string redirectUri, string clientState)
    {
        FacadeState state = new FacadeState(redirectUri, clientState, time.GetUtcNow() + Lifetime);
        return this.protector.Protect(JsonSerializer.Serialize(state));
    }

    /// <summary>The state, or null with the reason it was refused. The reason never echoes the state itself.</summary>
    public FacadeState? Unprotect(string? protectedState, out string? failure)
    {
        failure = null;
        if (string.IsNullOrEmpty(protectedState))
        {
            failure = "state is missing.";
            return null;
        }

        string json;
        try
        {
            json = this.protector.Unprotect(protectedState);
        }
        catch (CryptographicException)
        {
            failure = "state did not unprotect: altered, truncated, or protected under another key ring.";
            return null;
        }

        FacadeState? state;
        try
        {
            state = JsonSerializer.Deserialize<FacadeState>(json);
        }
        catch (JsonException)
        {
            failure = "state unprotected but is not a facade state.";
            return null;
        }

        if (state is null || string.IsNullOrEmpty(state.RedirectUri))
        {
            failure = "state unprotected but names no redirect_uri.";
            return null;
        }

        if (time.GetUtcNow() >= state.ExpiresAt)
        {
            failure = $"state expired at {state.ExpiresAt:O}.";
            return null;
        }

        return state;
    }
}
