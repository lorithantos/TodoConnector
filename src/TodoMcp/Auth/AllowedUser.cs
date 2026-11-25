using Microsoft.AspNetCore.Authorization;

namespace TodoMcp.Auth;

public sealed class AllowedUserRequirement : IAuthorizationRequirement;

/// <summary>
/// Decides whether a signed-in caller is on the TodoMcp:AllowedUsers list. An entry is
/// "&lt;tid&gt;:&lt;oid&gt;", and only the immutable tenant id and object id together identify a
/// caller: sign-in names (preferred_username, upn, email) are mutable and can be set by a
/// tenant admin, and an oid alone is not unique across tenants.
/// </summary>
public sealed class AllowedUserCheck(IOptionsMonitor<TodoMcpOptions> options)
{
    public const string PolicyName = "TodoUser";

    public bool IsAllowed(ClaimsPrincipal user)
    {
        string? key = CallerKey(user);
        if (key is null)
        {
            return false;
        }

        return options.CurrentValue.AllowedUsers.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The caller's allow-list entry, "&lt;tid&gt;:&lt;oid&gt;", or null when either claim is missing.</summary>
    public static string? CallerKey(ClaimsPrincipal user)
    {
        string? tid = user.GetTenantId();
        string? oid = user.GetObjectId();
        if (string.IsNullOrEmpty(tid) || string.IsNullOrEmpty(oid))
        {
            return null;
        }

        return $"{tid}:{oid}";
    }

    /// <summary>True when the entry is exactly two GUIDs (xxxxxxxx-xxxx-...) joined by a colon.</summary>
    public static bool IsWellFormed(string? entry)
    {
        string[] parts = (entry ?? "").Split(':');
        return parts.Length == 2
            && parts[0].Length == 36 && Guid.TryParseExact(parts[0], "D", out Guid _)
            && parts[1].Length == 36 && Guid.TryParseExact(parts[1], "D", out Guid _);
    }
}

/// <summary>Fails startup (with ValidateOnStart) on an allow-list entry that is not "&lt;tid&gt;:&lt;oid&gt;".</summary>
public sealed class AllowedUsersValidator : IValidateOptions<TodoMcpOptions>
{
    public ValidateOptionsResult Validate(string? name, TodoMcpOptions options)
    {
        List<string> failures = options.AllowedUsers
            .Where(e => !AllowedUserCheck.IsWellFormed(e))
            .Select(e => $"TodoMcp:AllowedUsers entry '{e}' is not <tenant id>:<object id> (two GUIDs joined by a colon).")
            .ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class AllowedUserHandler(AllowedUserCheck check, ILogger<AllowedUserHandler> logger)
    : AuthorizationHandler<AllowedUserRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AllowedUserRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        if (check.IsAllowed(context.User))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        string? key = AllowedUserCheck.CallerKey(context.User);
        string? name = context.User.FindFirstValue("preferred_username");
        if (key is null)
        {
            logger.LogWarning(
                "Refused caller (shown as {Name}, display only): the token has no tid or no oid claim, so it cannot match TodoMcp:AllowedUsers.",
                name);
        }
        else
        {
            logger.LogWarning(
                "Refused caller tid:oid={CallerKey} (shown as {Name}, display only). Add {Entry} to TodoMcp:AllowedUsers to let them in.",
                key, name, key);
        }
        return Task.CompletedTask;
    }
}
