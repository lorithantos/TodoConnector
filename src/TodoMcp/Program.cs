using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using TodoMcp;
using TodoMcp.Auth;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// On App Service, log to the platform's application log so `az webapp log tail` shows it -- the
// allow-list bootstrap depends on reading the refusal line there. Off App Service it does nothing.
builder.Logging.AddAzureWebAppDiagnostics();

builder.Services.AddOptions<TodoMcpOptions>()
    .Bind(builder.Configuration.GetSection(TodoMcpOptions.SectionName))
    .ValidateOnStart(); // a malformed TodoMcp:AllowedUsers entry stops the app rather than being ignored
builder.Services.AddSingleton<IValidateOptions<TodoMcpOptions>, AllowedUsersValidator>();

// Incoming tokens: issued by Entra for this app (api://{ClientId}). Outgoing: Graph via on-behalf-of.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi()
    .AddInMemoryTokenCaches();

builder.Services.AddSingleton<AllowedUserCheck>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AllowedUserCheck.PolicyName, policy => policy
        .RequireAuthenticatedUser()
        .AddRequirements(new AllowedUserRequirement()));
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AllowedUserHandler>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient(OAuthFacade.EntraHttpClient);
// The token relay's credential, decided from the same AzureAd section the on-behalf-of path reads.
builder.Services.AddSingleton(sp => EntraClientCredential.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IClientAssertionSource, ManagedIdentityAssertionSource>();
builder.Services.AddHttpClient<GraphTodoClient>(c => c.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"));
builder.Services.AddSingleton(TimeProvider.System); // the one place the real clock is named
builder.Services.AddSingleton<TodoClock>();

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "microsoft-todo", Version = "0.1.0" };
        o.ServerInstructions = ServerText.Instructions;
    })
    // Stateless: no server-side session, so restarts and scale-out never break Claude's connection.
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

WebApplication app = builder.Build();

// Resolved now, so an app with neither a managed identity nor a secret stops here, naming both settings,
// instead of at its first token relay.
_ = app.Services.GetRequiredService<EntraClientCredential>();

app.UseForwardedHeaders();
app.UseMcpChallengeHeader(); // adds resource_metadata to 401s so Claude can discover the OAuth endpoints
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Text("Microsoft To Do MCP server. MCP endpoint: /mcp"));
app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapOAuthFacade();
app.MapMcp("/mcp").RequireAuthorization(AllowedUserCheck.PolicyName);

app.Run();

public partial class Program;
