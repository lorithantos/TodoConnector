using Microsoft.AspNetCore.Authorization;

namespace TodoMcp.Tests;

public class AllowedUserTests
{
    private const string Tid = "aaaaaaaa-1111-4222-8333-444444444444";
    private const string Oid = "bbbbbbbb-5555-4666-8777-888888888888";
    private const string PersonalTid = "9188040d-6c67-4c5b-b112-36a304b66dad";
    private const string OtherOid = "cccccccc-9999-4aaa-8bbb-dddddddddddd";
    private const string Entry = Tid + ":" + Oid;

    private static ClaimsPrincipal User(string? tid, string? oid, string name = "me@example.com")
    {
        List<Claim> claims = [new Claim("preferred_username", name), new Claim(ClaimTypes.Upn, name)];
        if (tid is not null)
        {
            claims.Add(new Claim("tid", tid));
        }
        if (oid is not null)
        {
            claims.Add(new Claim("oid", oid));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    // Built directly, so these tests also hold for an entry startup validation would have refused.
    private static AllowedUserCheck Check(params string[] allowed) =>
        new(new StaticOptions<TodoMcpOptions>(new TodoMcpOptions { AllowedUsers = [.. allowed] }));

    [Fact]
    public void Tenant_and_object_id_together_let_the_caller_in() =>
        Assert.True(Check(PersonalTid + ":" + OtherOid, Entry).IsAllowed(User(Tid, Oid)));

    [Fact]
    public void The_right_object_id_in_another_tenant_is_refused() =>
        Assert.False(Check(Entry).IsAllowed(User(PersonalTid, Oid)));

    [Fact]
    public void The_right_tenant_with_another_object_id_is_refused() =>
        Assert.False(Check(Entry).IsAllowed(User(Tid, OtherOid)));

    [Fact]
    public void A_matching_sign_in_name_with_other_ids_is_refused() =>
        Assert.False(Check("me@example.com", Entry).IsAllowed(User(PersonalTid, OtherOid, "me@example.com")));

    [Fact]
    public void Entries_match_regardless_of_case() =>
        Assert.True(Check(Entry.ToUpperInvariant()).IsAllowed(User(Tid, Oid)));

    [Fact]
    public void A_caller_with_no_tenant_id_is_refused() =>
        Assert.False(Check(Entry).IsAllowed(User(null, Oid)));

    [Fact]
    public void A_caller_with_no_object_id_is_refused() =>
        Assert.False(Check(Entry).IsAllowed(User(Tid, null)));

    [Fact]
    public void An_empty_list_lets_nobody_in() =>
        Assert.False(Check().IsAllowed(User(Tid, Oid)));

    [Fact]
    public async Task A_refusal_logs_the_entry_ready_to_paste()
    {
        CapturingLogger<AllowedUserHandler> logger = new CapturingLogger<AllowedUserHandler>();
        AllowedUserHandler handler = new AllowedUserHandler(Check(), logger);
        AuthorizationHandlerContext context = new AuthorizationHandlerContext([new AllowedUserRequirement()], User(Tid, Oid), null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        string line = Assert.Single(logger.Lines);
        Assert.Contains($"Add {Entry} to TodoMcp:AllowedUsers", line);
        Assert.Contains("me@example.com", line);
    }

    [Theory]
    [InlineData("me@example.com")]
    [InlineData(Oid)]
    [InlineData(Entry + ":" + Oid)]
    [InlineData(Tid + ";" + Oid)]
    [InlineData(" " + Entry)]
    [InlineData("{" + Tid + "}:{" + Oid + "}")]
    [InlineData("")]
    public void The_validator_refuses_a_malformed_entry_by_name(string bad)
    {
        ValidateOptionsResult result = new AllowedUsersValidator().Validate(null, new TodoMcpOptions { AllowedUsers = [Entry, bad] });
        Assert.True(result.Failed);
        Assert.Contains($"'{bad}'", result.FailureMessage);
    }

    [Fact]
    public void The_validator_accepts_an_empty_list_and_well_formed_entries()
    {
        Assert.True(new AllowedUsersValidator().Validate(null, new TodoMcpOptions()).Succeeded);
        Assert.True(new AllowedUsersValidator().Validate(null, new TodoMcpOptions { AllowedUsers = [Entry, Entry.ToUpperInvariant()] }).Succeeded);
    }

    private sealed class MalformedListFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("AzureAd:ClientId", "11111111-2222-3333-4444-555555555555");
            builder.UseSetting("AzureAd:ClientSecret", "test-secret");
            builder.UseSetting("TodoMcp:AllowedUsers:0", Entry);
            builder.UseSetting("TodoMcp:AllowedUsers:1", "me@example.com");
        }
    }

    [Fact]
    public void A_malformed_entry_fails_startup_and_names_the_entry()
    {
        using MalformedListFactory factory = new MalformedListFactory();
        OptionsValidationException e = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("'me@example.com'", e.Message);
    }
}
