using System.Security.Claims;
using TodoMcp.Auth;

namespace TodoMcp.Tests;

public class AllowedUserTests
{
    private static ClaimsPrincipal User(string oid, string name) => new(new ClaimsIdentity(
    [
        new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", oid),
        new Claim("preferred_username", name),
    ], "test"));

    private static AllowedUserCheck Check(params string[] allowed) =>
        new(new StaticOptions<TodoMcpOptions>(new TodoMcpOptions { AllowedUsers = [.. allowed] }));

    [Fact] public void Empty_list_lets_nobody_in() => Assert.False(Check().IsAllowed(User("o1", "a@b.com")));
    [Fact] public void Matches_object_id() => Assert.True(Check("O1").IsAllowed(User("o1", "a@b.com")));
    [Fact] public void Matches_sign_in_name() => Assert.True(Check("A@b.com").IsAllowed(User("o1", "a@b.com")));
    [Fact] public void Refuses_others() => Assert.False(Check("someone@else.com").IsAllowed(User("o1", "a@b.com")));
}
