using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dochub.Api.Tests;

/// <summary>The Creator role comes only from Platform:Creators, and follows it both ways.</summary>
public class PlatformCreatorTests : PipelineTestBase
{
    private static IConfiguration Creators(params string[] emails) => new ConfigurationBuilder()
        .AddInMemoryCollection(emails.Select((e, i) => new KeyValuePair<string, string?>($"Platform:Creators:{i}", e)))
        .Build();

    [Fact]
    public async Task A_listed_address_becomes_a_creator_even_before_first_sign_in()
    {
        await DatabaseInitializer.ApplyCreatorsAsync(Db, Creators(" Someone@Example.com "), NullLogger.Instance);

        var user = await Db.Users.AsNoTracking().SingleAsync(u => u.Email == "someone@example.com");
        Assert.True(user.IsCreator);
        Assert.True(user.CanCreateOrganizations);
        Assert.Equal("invited", user.IdentityProvider);   // binds to the real account on first sign-in
        Assert.False(await Db.OrganizationMembers.AnyAsync(m => m.UserId == user.Id)); // no membership granted
    }

    [Fact]
    public async Task Removing_an_address_from_the_list_revokes_the_role()
    {
        await DatabaseInitializer.ApplyCreatorsAsync(Db, Creators("owner@test.local"), NullLogger.Instance);
        Assert.True((await Db.Users.AsNoTracking().SingleAsync(u => u.Id == UserId)).IsCreator);

        await DatabaseInitializer.ApplyCreatorsAsync(Db, Creators(), NullLogger.Instance);
        Db.ChangeTracker.Clear();
        Assert.False((await Db.Users.AsNoTracking().SingleAsync(u => u.Id == UserId)).IsCreator);
    }

    [Fact]
    public async Task An_owner_named_by_login_id_is_found_or_invited_once()
    {
        var invited = await PlatformEndpoints.FindOrInviteAsync(Db, "jane.doe@contoso.com", null, default);
        var again = await PlatformEndpoints.FindOrInviteAsync(Db, "jane.doe@contoso.com", "Jane", default);
        var existing = await PlatformEndpoints.FindOrInviteAsync(Db, "owner@test.local", null, default);

        Assert.Equal(invited.Id, again.Id);
        Assert.Equal("Jane Doe", invited.DisplayName);
        Assert.Equal(UserId, existing.Id);
    }

    [Theory]
    [InlineData("name@company.com", true)]
    [InlineData("first.last@outlook.com", true)]
    [InlineData("user@tenant.onmicrosoft.com", true)]
    [InlineData("not an email", false)]
    [InlineData("Jane <jane@x.com>", false)]
    [InlineData("jane@", false)]
    public void Login_ids_must_be_bare_sign_in_addresses(string value, bool valid) =>
        Assert.Equal(valid, PlatformEndpoints.IsLoginId(value));
}
