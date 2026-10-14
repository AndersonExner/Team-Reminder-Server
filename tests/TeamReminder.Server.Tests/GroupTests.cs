using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamReminder.Server;

namespace TeamReminder.Server.Tests;

public sealed class GroupTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record User(HttpClient Client, SessionResponse Session) : IDisposable { public void Dispose() => Client.Dispose(); }
    private async Task<User> NewUser()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("Group user", $"groups-{Guid.NewGuid():N}@example.com", "strong test password"));
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return new(client, session);
    }
    private async Task<GroupSummary> Create(User owner)
    {
        var response = await owner.Client.PostAsJsonAsync("/api/groups", new GroupCreateRequest("Desenvolvimento DigitalDesk", "Equipe de desenvolvimento"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GroupSummary>())!;
    }
    private static async Task<GroupDetails> Details(User user, Guid id) => (await user.Client.GetFromJsonAsync<GroupDetails>($"/api/groups/{id}"))!;
    private static async Task<GroupInvitationResponse> Invite(User owner, Guid id, User recipient)
    {
        var revision = (await Details(owner, id)).Group.Revision;
        var response = await owner.Client.PostAsJsonAsync($"/api/groups/{id}/invitations", new GroupInviteRequest(recipient.Session.User.Email.ToUpperInvariant(), revision));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GroupInvitationResponse>())!;
    }
    private static async Task Join(User owner, Guid id, User member)
    {
        var invite = await Invite(owner, id, member);
        Assert.Equal(HttpStatusCode.NoContent, (await member.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
    }
    private static async Task Promote(User owner, Guid id, User member)
    {
        var revision = (await Details(owner, id)).Group.Revision;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.PatchAsJsonAsync($"/api/groups/{id}/members/{member.Session.User.Id}", new GroupRoleRequest(GroupRole.Administrator, revision))).StatusCode);
    }

    [Fact]
    public async Task CreatorIsOwnerAndOutsidersCannotReadTheGroup()
    {
        using var owner = await NewUser(); using var outsider = await NewUser();
        var team = await Create(owner);
        Assert.Equal(GroupRole.Owner, team.MyRole); Assert.Equal(owner.Session.User.Id, team.OwnerAccountId);
        var details = await Details(owner, team.Id);
        Assert.Equal(GroupRole.Owner, Assert.Single(details.Members).Role);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Client.GetAsync($"/api/groups/{team.Id}")).StatusCode);
        Assert.Empty((await outsider.Client.GetFromJsonAsync<GroupSummary[]>("/api/groups"))!);
        Assert.Equal("Created", Assert.Single((await owner.Client.GetFromJsonAsync<GroupAuditResponse[]>($"/api/groups/{team.Id}/audit"))!).Action);
    }

    [Fact]
    public async Task InvitationRequiresAcceptanceByItsRecipientAndAcceptIsIdempotent()
    {
        using var owner = await NewUser(); using var member = await NewUser(); using var outsider = await NewUser();
        var team = await Create(owner); var invite = await Invite(owner, team.Id, member);
        Assert.Single((await member.Client.GetFromJsonAsync<GroupInvitationResponse[]>("/api/invitations"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.GetAsync($"/api/groups/{team.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
        var revision = (await Details(member, team.Id)).Group.Revision;
        Assert.Equal(HttpStatusCode.NoContent, (await member.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
        var joined = await Details(member, team.Id);
        Assert.Equal(revision, joined.Group.Revision); Assert.Equal(2, joined.Members.Count);
        Assert.Equal(GroupRole.Member, joined.Group.MyRole); Assert.Empty(joined.Invitations);
    }

    [Fact]
    public async Task MembersCannotAdministerAndDeniedWritesDoNotAdvanceRevision()
    {
        using var owner = await NewUser(); using var member = await NewUser();
        var team = await Create(owner); await Join(owner, team.Id, member);
        var revision = (await Details(member, team.Id)).Group.Revision;
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.PatchAsJsonAsync($"/api/groups/{team.Id}", new GroupEditRequest("Changed", "", revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.PostAsJsonAsync($"/api/groups/{team.Id}/invitations", new GroupInviteRequest("someone@example.com", revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.PatchAsJsonAsync($"/api/groups/{team.Id}/members/{member.Session.User.Id}", new GroupRoleRequest(GroupRole.Administrator, revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.DeleteAsync($"/api/groups/{team.Id}?revision={revision}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.GetAsync($"/api/groups/{team.Id}/audit")).StatusCode);
        Assert.Equal(revision, (await Details(owner, team.Id)).Group.Revision);
    }

    [Fact]
    public async Task AdminCanInviteButCannotRemoveOwnerOrDeleteGroup()
    {
        using var owner = await NewUser(); using var admin = await NewUser(); using var recipient = await NewUser();
        var team = await Create(owner); await Join(owner, team.Id, admin); await Promote(owner, team.Id, admin);
        Assert.Equal(GroupRole.Administrator, (await Details(admin, team.Id)).Group.MyRole);
        await Invite(admin, team.Id, recipient);
        var revision = (await Details(admin, team.Id)).Group.Revision;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Client.DeleteAsync($"/api/groups/{team.Id}/members/{owner.Session.User.Id}?revision={revision}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Client.PatchAsJsonAsync($"/api/groups/{team.Id}/members/{owner.Session.User.Id}", new GroupRoleRequest(GroupRole.Member, revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.DeleteAsync($"/api/groups/{team.Id}?revision={revision}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.PostAsJsonAsync($"/api/groups/{team.Id}/transfer", new GroupTransferRequest(admin.Session.User.Id, revision))).StatusCode);
    }

    [Fact]
    public async Task OwnerMustTransferBeforeLeavingAndThereIsExactlyOneOwner()
    {
        using var owner = await NewUser(); using var next = await NewUser();
        var team = await Create(owner); await Join(owner, team.Id, next);
        var revision = (await Details(owner, team.Id)).Group.Revision;
        Assert.Equal(HttpStatusCode.Conflict, (await owner.Client.PostAsJsonAsync($"/api/groups/{team.Id}/leave", new GroupRevisionRequest(revision))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.PatchAsJsonAsync($"/api/groups/{team.Id}/members/{next.Session.User.Id}", new GroupRoleRequest(GroupRole.Owner, revision))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.PostAsJsonAsync($"/api/groups/{team.Id}/transfer", new GroupTransferRequest(next.Session.User.Id, revision))).StatusCode);
        var transferred = await Details(next, team.Id);
        Assert.Equal(next.Session.User.Id, transferred.Group.OwnerAccountId);
        Assert.Single(transferred.Members, x => x.Role == GroupRole.Owner);
        Assert.Equal(GroupRole.Administrator, transferred.Members.Single(x => x.AccountId == owner.Session.User.Id).Role);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.PostAsJsonAsync($"/api/groups/{team.Id}/leave", new GroupRevisionRequest(transferred.Group.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/groups/{team.Id}")).StatusCode);
    }

    [Fact]
    public async Task ConcurrentEditsHaveOneWinnerAndOneConflict()
    {
        using var owner = await NewUser(); var team = await Create(owner);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => owner.Client.PatchAsJsonAsync($"/api/groups/{team.Id}", new GroupEditRequest("Name " + i, "", 1))));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(2, (await Details(owner, team.Id)).Group.Revision);
        Assert.Equal(2, (await owner.Client.GetFromJsonAsync<GroupAuditResponse[]>($"/api/groups/{team.Id}/audit"))!.Length);
    }

    [Fact]
    public async Task ExpiredInvitationCannotBeAcceptedAndCanBeReplaced()
    {
        using var owner = await NewUser(); using var member = await NewUser();
        var team = await Create(owner); var invite = await Invite(owner, team.Id, member);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
        await db.GroupInvitations.Where(x => x.Id == invite.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Empty((await member.Client.GetFromJsonAsync<GroupInvitationResponse[]>("/api/invitations"))!);
        Assert.Equal(HttpStatusCode.Gone, (await member.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
        Assert.NotEqual(invite.Id, (await Invite(owner, team.Id, member)).Id);
    }

    [Fact]
    public async Task DeclinedAndCanceledInvitationsCannotGrantMembership()
    {
        using var owner = await NewUser(); using var member = await NewUser();
        var team = await Create(owner); var first = await Invite(owner, team.Id, member);
        Assert.Equal(HttpStatusCode.NoContent, (await member.Client.PostAsync($"/api/invitations/{first.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await member.Client.PostAsync($"/api/invitations/{first.Id}/accept", null)).StatusCode);
        var second = await Invite(owner, team.Id, member);
        var revision = (await Details(owner, team.Id)).Group.Revision;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.PostAsJsonAsync($"/api/groups/{team.Id}/invitations/{second.Id}/cancel", new GroupRevisionRequest(revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await member.Client.PostAsync($"/api/invitations/{second.Id}/accept", null)).StatusCode);
        Assert.Single((await Details(owner, team.Id)).Members);
    }

    [Fact]
    public async Task RemovedMemberCannotRejoinUsingAnAlreadyAcceptedInvitation()
    {
        using var owner = await NewUser(); using var member = await NewUser();
        var team = await Create(owner); var invite = await Invite(owner, team.Id, member);
        await member.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null);
        var revision = (await Details(owner, team.Id)).Group.Revision;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/groups/{team.Id}/members/{member.Session.User.Id}?revision={revision}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.GetAsync($"/api/groups/{team.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await member.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovingAdministrationCancelsOutstandingInvitations(bool remove)
    {
        using var owner = await NewUser(); using var admin = await NewUser(); using var recipient = await NewUser();
        var team = await Create(owner); await Join(owner, team.Id, admin); await Promote(owner, team.Id, admin);
        var invite = await Invite(admin, team.Id, recipient);
        var revision = (await Details(owner, team.Id)).Group.Revision;
        var response = remove ? await owner.Client.DeleteAsync($"/api/groups/{team.Id}/members/{admin.Session.User.Id}?revision={revision}")
            : await owner.Client.PatchAsJsonAsync($"/api/groups/{team.Id}/members/{admin.Session.User.Id}", new GroupRoleRequest(GroupRole.Member, revision));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await recipient.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task DeletedGroupsDisappearForMembersAndInvitees()
    {
        using var owner = await NewUser(); using var member = await NewUser(); using var pending = await NewUser();
        var team = await Create(owner); await Join(owner, team.Id, member); var invite = await Invite(owner, team.Id, pending);
        var revision = (await Details(owner, team.Id)).Group.Revision;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/groups/{team.Id}?revision={revision}")).StatusCode);
        Assert.Empty((await member.Client.GetFromJsonAsync<GroupSummary[]>("/api/groups"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.GetAsync($"/api/groups/{team.Id}")).StatusCode);
        Assert.Empty((await pending.Client.GetFromJsonAsync<GroupInvitationResponse[]>("/api/invitations"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await pending.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task GroupValidationAndOwnerLimitAreEnforced()
    {
        using var owner = await NewUser();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.PostAsJsonAsync("/api/groups", new GroupCreateRequest("", ""))).StatusCode);
        for (var i = 0; i < 20; i++) await Create(owner);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.Client.PostAsJsonAsync("/api/groups", new GroupCreateRequest("21", ""))).StatusCode);
    }

    [Fact]
    public async Task EveryGroupRouteRequiresAuthentication()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/invitations")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/groups", new GroupCreateRequest("Team", ""))).StatusCode);
    }

    [Fact]
    public async Task ReciprocalInvitationsFromDifferentGroupsDoNotDeadlock()
    {
        using var first = await NewUser(); using var second = await NewUser();
        var a = await Create(first); var b = await Create(second);
        var responses = await Task.WhenAll(
            first.Client.PostAsJsonAsync($"/api/groups/{a.Id}/invitations", new GroupInviteRequest(second.Session.User.Email, 1)),
            second.Client.PostAsJsonAsync($"/api/groups/{b.Id}/invitations", new GroupInviteRequest(first.Session.User.Email, 1)));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
    }

    [Fact]
    public async Task MemberLimitRejectsAcceptanceWithoutConsumingTheInvitation()
    {
        using var owner = await NewUser(); using var recipient = await NewUser();
        var team = await Create(owner); var invite = await Invite(owner, team.Id, recipient);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
        for (var i = 0; i < 99; i++)
        {
            var email = $"seed-{Guid.NewGuid():N}@example.com";
            var account = new Account { Name = "Seeded member", Email = email, NormalizedEmail = email.ToUpperInvariant() };
            db.Accounts.Add(account);
            db.GroupMemberships.Add(new() { GroupId = team.Id, AccountId = account.Id, Role = GroupRole.Member, JoinedAt = DateTimeOffset.UtcNow });
        }
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await recipient.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
        Assert.Single((await recipient.Client.GetFromJsonAsync<GroupInvitationResponse[]>("/api/invitations"))!);
        var seeded = await db.GroupMemberships.FirstAsync(x => x.GroupId == team.Id && x.AccountId != owner.Session.User.Id);
        db.GroupMemberships.Remove(seeded); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await recipient.Client.PostAsync($"/api/invitations/{invite.Id}/accept", null)).StatusCode);
    }
}
