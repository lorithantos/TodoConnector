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
///   * receives Entra's answer at /oauth/callback, logs it, and forwards it to Claude's redirect_uri,
///   * relays /oauth/token to Entra, adding the app's client credential (see EntraClientCredential).
/// It stores nothing: Claude's redirect_uri and state travel through Entra inside the protected
/// state (OAuthStateProtector). Entra still does the sign-in, consent, PKCE check and token
/// issuing, and only redirects to /oauth/callback, the one reply URL registered on the app.
/// Every refusal is logged at Warning with its reason; no code, token or credential is logged.
/// </summary>
public static class OAuthFacade
{
    public const string EntraHttpClient = "entra";
    public const string PublicClientId = "claude-connector";
    public const string CallbackPath = "/oauth/callback";

    public static IEndpointRouteBuilder MapOAuthFacade(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResourceMetadata);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", ProtectedResourceMetadata);
        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServerMetadata);
        app.MapGet("/.well-known/openid-configuration", AuthorizationServerMetadata);
        app.MapPost("/oauth/register", Register);
        app.MapGet("/oauth/authorize", Authorize);
        app.MapGet(CallbackPath, Callback);
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

    private static async Task<IResult> Register(HttpContext ctx, IOptionsMonitor<TodoMcpOptions> options, TimeProvider time,
        ILoggerFactory loggers)
    {
        ILogger log = Logger(loggers);
        JsonElement body;
        try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
        catch (JsonException)
        {
            log.LogWarning("OAuth register refused: the body is not JSON.");
            return OAuthError("invalid_client_metadata", "Body must be JSON.");
        }

        string[] redirectUris = body.TryGetProperty("redirect_uris", out JsonElement r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : [];
        if (redirectUris.Length == 0 || !redirectUris.All(u => IsAllowedRedirect(u, options.CurrentValue)))
        {
            log.LogWarning("OAuth register refused: redirect_uris [{RedirectUris}] are not all on the allow-list (TodoMcp:AllowedRedirectUris).",
                string.Join(", ", redirectUris));
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

    private static IResult Authorize(HttpContext ctx, IConfiguration config, IOptionsMonitor<TodoMcpOptions> options,
        OAuthStateProtector states, ILoggerFactory loggers)
    {
        ILogger log = Logger(loggers);
        IQueryCollection q = ctx.Request.Query;
        string redirectUri = q["redirect_uri"].ToString();
        if (!IsAllowedRedirect(redirectUri, options.CurrentValue))
        {
            log.LogWarning("OAuth authorize refused: redirect_uri {RedirectUri} is not on the allow-list (TodoMcp:AllowedRedirectUris).", redirectUri);
            return Results.BadRequest("redirect_uri is not allowed.");
        }

        if (q["response_type"] != "code")
        {
            log.LogWarning("OAuth authorize refused: response_type {ResponseType} is not code.", q["response_type"].ToString());
            return Results.BadRequest("Only response_type=code is supported.");
        }

        if (string.IsNullOrEmpty(q["code_challenge"]) || q["code_challenge_method"] != "S256")
        {
            log.LogWarning("OAuth authorize refused: PKCE with S256 is required (code_challenge present: {HasChallenge}, code_challenge_method {Method}).",
                !string.IsNullOrEmpty(q["code_challenge"]), q["code_challenge_method"].ToString());
            return Results.BadRequest("PKCE with S256 is required.");
        }

        EntraSettings entra = EntraSettings.From(config);
        string state = states.Protect(redirectUri, q["state"].ToString());
        string url = BuildEntraAuthorizeUrl(entra, CallbackUrl(ctx), state, q["code_challenge"].ToString());
        return Results.Redirect(url);
    }

    /// <summary>
    /// Entra's authorize URL. <paramref name="redirectUri"/> is this server's /oauth/callback and
    /// <paramref name="state"/> the protected facade state, never Claude's own. prompt=select_account
    /// shows the account picker, so a browser signed in to Windows does not sign in silently with
    /// that account, and Entra's own errors are shown on Microsoft's page.
    /// </summary>
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
            ["prompt"] = "select_account",
        };
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{entra.AuthorityUrl}/oauth2/v2.0/authorize", p);
    }

    // ---- callback: Entra's answer, logged, then forwarded to Claude ------------------------

    private static IResult Callback(HttpContext ctx, IOptionsMonitor<TodoMcpOptions> options, OAuthStateProtector states,
        ILoggerFactory loggers)
    {
        ILogger log = Logger(loggers);
        IQueryCollection q = ctx.Request.Query;
        string code = q["code"].ToString();
        string error = q["error"].ToString();
        string description = q["error_description"].ToString();
        ctx.Response.Headers.CacheControl = "no-store";

        FacadeState? state = states.Unprotect(q["state"].ToString(), out string? failure);
        if (state is not null && !IsAllowedRedirect(state.RedirectUri, options.CurrentValue))
        {
            failure = $"state names redirect_uri {state.RedirectUri}, which is not on the allow-list (TodoMcp:AllowedRedirectUris).";
            state = null;
        }

        if (state is null)
        {
            // Nowhere validated to send the answer, so it goes nowhere: an unvalidated redirect is an open redirect.
            log.LogWarning("OAuth callback refused, nothing forwarded: {Reason} Entra's error, if any: {Error} {Description}",
                failure, error, description);
            return Results.BadRequest("This sign-in response does not match a sign-in this server started, or it took longer than 10 minutes. Connect again from Claude.");
        }

        Dictionary<string, string?> forward = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(error))
        {
            forward["error"] = error;
            if (!string.IsNullOrEmpty(description))
            {
                forward["error_description"] = description;
            }
            log.LogWarning("Entra refused the sign-in: {Error} {Description} Forwarding the error to {RedirectUri}.",
                error, description, state.RedirectUri);
        }
        else if (!string.IsNullOrEmpty(code))
        {
            forward["code"] = code;
            log.LogInformation("Entra returned an authorization code; forwarding it to {RedirectUri}.", state.RedirectUri);
        }
        else
        {
            forward["error"] = "server_error";
            forward["error_description"] = "Entra's response carried neither a code nor an error.";
            log.LogWarning("Entra's response carried neither a code nor an error. Forwarding server_error to {RedirectUri}.", state.RedirectUri);
        }

        if (!string.IsNullOrEmpty(state.ClientState))
        {
            forward["state"] = state.ClientState;
        }
        return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(state.RedirectUri, forward));
    }

    // ---- token: relay to Entra with the app's client credential ---------------------------

    private static async Task Token(HttpContext ctx, IConfiguration config, IHttpClientFactory http,
        IOptionsMonitor<TodoMcpOptions> options, ILoggerFactory loggers,
        EntraClientCredential credential, IClientAssertionSource assertions)
    {
        ILogger log = Logger(loggers);
        IFormCollection form = await ctx.Request.ReadFormAsync();
        EntraSettings entra = EntraSettings.From(config);
        IReadOnlyDictionary<string, string> incoming = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        IReadOnlyDictionary<string, string> credentialFields = await credential.FormFieldsAsync(assertions, entra, ctx.RequestAborted);

        Dictionary<string, string>? upstream = BuildEntraTokenForm(incoming, entra, credentialFields, options.CurrentValue, CallbackUrl(ctx), out string? error);
        ctx.Response.Headers.CacheControl = "no-store";
        if (upstream is null)
        {
            incoming.TryGetValue("grant_type", out string? grantType);
            log.LogWarning("OAuth token refused (grant_type {GrantType}): {Reason}", grantType, error);
            await OAuthError("invalid_request", error!).ExecuteAsync(ctx);
            return;
        }

        using HttpResponseMessage response = await http.CreateClient(EntraHttpClient).PostAsync(
            $"{entra.AuthorityUrl}/oauth2/v2.0/token", new FormUrlEncodedContent(upstream), ctx.RequestAborted);
        string body = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            log.LogWarning("Entra token endpoint returned {Status}: {Body}", (int)response.StatusCode, body);
        }

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body, ctx.RequestAborted);
    }

    /// <summary>
    /// Builds the form sent to Entra's token endpoint, or null with an error message.
    /// <paramref name="credential"/> is the app's credential as form fields (EntraClientCredential.FormFieldsAsync).
    /// <paramref name="callbackUrl"/> is this server's /oauth/callback: Entra requires the code
    /// redemption's redirect_uri to equal the authorize request's, which was the callback, not
    /// Claude's own redirect_uri (still checked against the allow-list).
    /// </summary>
    public static Dictionary<string, string>? BuildEntraTokenForm(
        IReadOnlyDictionary<string, string> incoming, EntraSettings entra, IReadOnlyDictionary<string, string> credential,
        TodoMcpOptions options, string callbackUrl, out string? error)
    {
        error = null;
        incoming.TryGetValue("grant_type", out string? grantType);
        Dictionary<string, string> form = new Dictionary<string, string>
        {
            ["client_id"] = entra.ClientId,
            ["scope"] = entra.Scope,
            ["grant_type"] = grantType ?? "",
        };
        foreach (KeyValuePair<string, string> field in credential)
        {
            form[field.Key] = field.Value;
        }

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
                form["redirect_uri"] = callbackUrl;
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

    /// <summary>Where Entra sends its answer: the one reply URL on the app registration.</summary>
    public static string CallbackUrl(HttpContext ctx) => $"{BaseUrl(ctx)}{CallbackPath}";

    private static ILogger Logger(ILoggerFactory loggers) => loggers.CreateLogger(typeof(OAuthFacade));

    private static IResult OAuthError(string error, string description) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description },
            statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>The Entra app registration, read from the AzureAd section. Its credential is EntraClientCredential.</summary>
public sealed record EntraSettings(string Instance, string TenantId, string ClientId)
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
            s["ClientId"] ?? throw new InvalidOperationException("AzureAd:ClientId is not set."));
    }
}
