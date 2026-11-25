namespace TodoMcp;

/// <summary>Settings bound from the "TodoMcp" configuration section.</summary>
public sealed class TodoMcpOptions
{
    public const string SectionName = "TodoMcp";

    /// <summary>
    /// Public https base URL of this server, e.g. https://todo-mcp-lori.azurewebsites.net.
    /// When empty, it is derived from the incoming request (works behind App Service).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Who may use the connector. Each entry is "&lt;tid&gt;:&lt;oid&gt;": the caller's Entra tenant id
    /// and object id, two GUIDs joined by a colon. Sign-in names are not accepted, and a
    /// malformed entry fails startup. Empty means nobody: the server refuses everyone and
    /// logs the caller's entry ready to paste so you can add yourself.
    /// </summary>
    public List<string> AllowedUsers { get; set; } = [];

    /// <summary>
    /// Time zone for due dates and reminders. IANA ("America/Los_Angeles") or Windows
    /// ("Pacific Standard Time") ids both work.
    /// </summary>
    public string TimeZone { get; set; } = "America/Los_Angeles";

    /// <summary>OAuth redirect URIs the authorization facade accepts (Claude's callbacks).</summary>
    public List<string> AllowedRedirectUris { get; set; } =
    [
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback",
    ];
}
