using Microsoft.AspNetCore.Authorization;

namespace TodoMcp.Auth;

public sealed class AllowedUserRequirement : IAuthorizationRequirement;

/// <summary>Decides whether a signed-in caller is on the TodoMcp:AllowedUsers list.</summary>
public sealed class AllowedUserCheck(IOptionsMonitor<TodoMcpOptions> options)
{
    public const string PolicyName = "TodoUser";

    public bool IsAllowed(ClaimsPrincipal user)
    {
        List<string> allowed = options.CurrentValue.AllowedUsers;
        string? oid = user.GetObjectId();
        string? name = user.FindFirstValue("preferred_username") ?? user.FindFirstValue(ClaimTypes.Upn);
        return allowed.Any(a =>
            (oid is not null && string.Equals(a, oid, StringComparison.OrdinalIgnoreCase)) ||
            (name is not null && string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));
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
        }
        else
        {
            logger.LogWarning(
                "Refused caller oid={Oid} name={Name}. Add one of these to TodoMcp:AllowedUsers to let them in.",
                context.User.GetObjectId(), context.User.FindFirstValue("preferred_username"));
        }
        return Task.CompletedTask;
    }
}
