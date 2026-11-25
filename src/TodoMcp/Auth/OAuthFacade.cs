namespace TodoMcp.Auth;

/// <summary>
/// A thin OAuth 2.1 authorization-server facade in front of Microsoft Entra ID.
///
/// Why it exists: Claude's connector client follows the MCP auth spec (dynamic client
/// registration, RFC 8707 "resource" parameter, scopes from the server's metadata). Entra has no
/// dynamic registration and rejects the "resource" parameter on its v2 endpoints, so pointing
/// Claude straight at Entra fails. The facade:
///   * answers dynamic registration with a fixed public client id (Claude never sees a secret),
///   * redirects /oauth/authorize to Entra with the real client id and a fixed scope, dropping "resource",
///   * relays /oauth/token to Entra, adding the client secret.
/// It stores nothing. Entra still does the sign-in, consent, PKCE check and token issuing,
/// and Entra only redirects to the reply URLs registered on the app.
/// </summary>
public static class OAuthFacade
{
    public const string EntraHttpClient = "entra";
    public const string PublicClientId = "claude-connector";

    public static IEndpointRouteBuilder MapOAuthFacade(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResourceMetadata);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", ProtectedResourceMetadata);
        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServerMetadata);
        app.MapGet("/.well-known/openid-configuration", AuthorizationServerMetadata);
        app.MapPost("/oauth/register", Register);
        app.MapGet("/oauth/authorize", Authorize);
        app.MapPost("/oauth/token", Token).DisableAntiforgery();
        return app;
    }

    /// <summary>Adds the RFC 9728 resource_metadata pointer to every 401 from the MCP endpoint.</summary>
    public static IApplicationBuilder UseMcpChallengeHeader(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                if (ctx.Response.StatusCode == StatusCodes.Status401Unauthorized &&
                    ctx.Request.Path.StartsWithSegments("/mcp"))
                {
                    string baseUrl = BaseUrl(ctx);
                    ctx.Response.Headers.WWWAuthenticate =
                        $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource/mcp\"";
                }
                return Task.CompletedTask;
            });
            await next();
        });

    // ---- metadata ------------------------------------------------------------------------

    private static IResult ProtectedResourceMetadata(HttpContext ctx)
    {
        string baseUrl = BaseUrl(ctx);
        return Results.Json(new Dictionary<string, object>
        {
            ["resource"] = $"{baseUrl}/mcp",
            ["authorization_servers"] = new[] { baseUrl },
            ["bearer_methods_supported"] = new[] { "header" },
            ["resource_name"] = "Microsoft To Do",
        });
    }

    private static IResult AuthorizationServerMetadata(HttpContext ctx)
    {
        string baseUrl = BaseUrl(ctx);
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = baseUrl,
            ["authorization_endpoint"] = $"{baseUrl}/oauth/authorize",
            ["token_endpoint"] = $"{baseUrl}/oauth/token",
            ["registration_endpoint"] = $"{baseUrl}/oauth/register",
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none", "client_secret_post", "client_secret_basic" },
            ["scopes_supported"] = new[] { "todo" },
        });
    }

    // ---- dynamic client registration (RFC 7591) ------------------------------------------

    private static async Task<IResult> Register(HttpContext ctx, IOptionsMonitor<TodoMcpOptions> options, TimeProvider time)
    {
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch (JsonException) { return OAuthError("invalid_client_metadata", "Body must be JSON."); }

        string[] redirectUris = body.TryGetProperty("redirect_uris", out JsonElement r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : [];
        if (redirectUris.Length == 0 || !redirectUris.All(u => IsAllowedRedirect(u, options.CurrentValue)))
        {
            return OAuthError("invalid_redirect_uri", "Only Claude's OAuth callback URLs are accepted.");
        }

        return Results.Json(new Dictionary<string, object>
        {
            ["client_id"] = PublicClientId,
            ["client_id_issued_at"] = time.GetUtcNow().ToUnixTimeSeconds(),
            ["redirect_uris"] = redirectUris,
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["client_name"] = body.TryGetProperty("client_name", out JsonElement n) ? n.GetString() ?? "Claude" : "Claude",
        }, statusCode: StatusCodes.Status201Created);
    }

    // ---- authorize: redirect to Entra -----------------------------------------------------

    private static IResult Authorize(HttpContext ctx, IConfiguration config, IOptionsMonitor<TodoMcpOptions> options)
    {
        IQueryCollection q = ctx.Request.Query;
        string redirectUri = q["redirect_uri"].ToString();
        if (!IsAllowedRedirect(redirectUri, options.CurrentValue))
        {
            return Results.BadRequest("redirect_uri is not allowed.");
        }

        if (q["response_type"] != "code")
        {
            return Results.BadRequest("Only response_type=code is supported.");
        }

        if (string.IsNullOrEmpty(q["code_challenge"]) || q["code_challenge_method"] != "S256")
        {
            return Results.BadRequest("PKCE with S256 is required.");
        }

        EntraSettings entra = EntraSettings.From(config);
        string url = BuildEntraAuthorizeUrl(entra, redirectUri, q["state"].ToString(), q["code_challenge"].ToString());
        return Results.Redirect(url);
    }

    public static string BuildEntraAuthorizeUrl(EntraSettings entra, string redirectUri, string state, string codeChallenge)
    {
        Dictionary<string, string?> p = new Dictionary<string, string?>
        {
            ["client_id"] = entra.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = entra.Scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{entra.AuthorityUrl}/oauth2/v2.0/authorize", p);
    }

    // ---- token: relay to Entra with the client secret -------------------------------------

    private static async Task Token(HttpContext ctx, IConfiguration config, IHttpClientFactory http,
        IOptionsMonitor<TodoMcpOptions> options, ILoggerFactory loggers)
    {
        IFormCollection form = await ctx.Request.ReadFormAsync();
        EntraSettings entra = EntraSettings.From(config);

        Dictionary<string, string>? upstream = BuildEntraTokenForm(form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()), entra, options.CurrentValue, out string? error);
        ctx.Response.Headers.CacheControl = "no-store";
        if (upstream is null)
        {
            await OAuthError("invalid_request", error!).ExecuteAsync(ctx);
            return;
        }

        using HttpResponseMessage response = await http.CreateClient(EntraHttpClient).PostAsync(
            $"{entra.AuthorityUrl}/oauth2/v2.0/token", new FormUrlEncodedContent(upstream), ctx.RequestAborted);
        string body = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            loggers.CreateLogger(typeof(OAuthFacade)).LogWarning("Entra token endpoint returned {Status}: {Body}", (int)response.StatusCode, body);
        }

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body, ctx.RequestAborted);
    }

    /// <summary>Builds the form sent to Entra's token endpoint, or null with an error message.</summary>
    public static Dictionary<string, string>? BuildEntraTokenForm(
        IReadOnlyDictionary<string, string> incoming, EntraSettings entra, TodoMcpOptions options, out string? error)
    {
        error = null;
        incoming.TryGetValue("grant_type", out string? grantType);
        Dictionary<string, string> form = new Dictionary<string, string>
        {
            ["client_id"] = entra.ClientId,
            ["client_secret"] = entra.ClientSecret,
            ["scope"] = entra.Scope,
            ["grant_type"] = grantType ?? "",
        };

        switch (grantType)
        {
            case "authorization_code":
                if (!incoming.TryGetValue("code", out string? code) || string.IsNullOrEmpty(code) ||
                    !incoming.TryGetValue("code_verifier", out string? verifier) || string.IsNullOrEmpty(verifier) ||
                    !incoming.TryGetValue("redirect_uri", out string? redirect) || !IsAllowedRedirect(redirect, options))
                {
                    error = "code, code_verifier and an allowed redirect_uri are required.";
                    return null;
                }
                form["code"] = code;
                form["code_verifier"] = verifier;
                form["redirect_uri"] = redirect;
                return form;

            case "refresh_token":
                if (!incoming.TryGetValue("refresh_token", out string? refresh) || string.IsNullOrEmpty(refresh))
                {
                    error = "refresh_token is required.";
                    return null;
                }
                form["refresh_token"] = refresh;
                return form;

            default:
                error = "Unsupported grant_type.";
                return null;
        }
        // Note: "resource", "client_id" and any client credentials Claude sends are deliberately dropped.
    }

    // ---- helpers ---------------------------------------------------------------------------

    public static bool IsAllowedRedirect(string? uri, TodoMcpOptions options) =>
        !string.IsNullOrEmpty(uri) && options.AllowedRedirectUris.Contains(uri, StringComparer.Ordinal);

    public static string BaseUrl(HttpContext ctx)
    {
        string? configured = ctx.RequestServices.GetRequiredService<IOptionsMonitor<TodoMcpOptions>>().CurrentValue.PublicBaseUrl;
        return !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimEnd('/')
            : $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}".TrimEnd('/');
    }

    private static IResult OAuthError(string error, string description) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description },
            statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>The Entra app registration, read from the AzureAd section.</summary>
public sealed record EntraSettings(string Instance, string TenantId, string ClientId, string ClientSecret)
{
    public string AuthorityUrl => $"{this.Instance.TrimEnd('/')}/{this.TenantId}";

    /// <summary>
    /// "/.default" on our own API gives a token for this app and, in the same consent prompt, the
    /// Graph permissions configured on the registration (Tasks.ReadWrite), which on-behalf-of needs.
    /// </summary>
    public string Scope => $"openid offline_access api://{this.ClientId}/.default";

    public static EntraSettings From(IConfiguration config)
    {
        IConfigurationSection s = config.GetSection("AzureAd");
        return new EntraSettings(
            s["Instance"] ?? "https://login.microsoftonline.com/",
            s["TenantId"] ?? "common",
            s["ClientId"] ?? throw new InvalidOperationException("AzureAd:ClientId is not set."),
            s["ClientSecret"] ?? throw new InvalidOperationException("AzureAd:ClientSecret is not set."));
    }
}
