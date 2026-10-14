using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamReminder.Server;
namespace TeamReminder.Server.Tests;
public sealed class GroupAlarmTests(ApiFactory factory):IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Client,SessionResponse Session)> User()
    {
        var c=factory.CreateClient();var r=await c.PostAsJsonAsync("/api/auth/register",new RegisterRequest("Group alarm","ga-"+Guid.NewGuid()+"@example.com","strong test password"));
        r.EnsureSuccessStatusCode();var s=(await r.Content.ReadFromJsonAsync<SessionResponse>())!;c.DefaultRequestHeaders.Authorization=new("Bearer",s.AccessToken);return(c,s);
    }
    private async Task<GroupSummary> Create(HttpClient owner)=>
        (await(await owner.PostAsJsonAsync("/api/groups",new GroupCreateRequest("Shared",""))).Content.ReadFromJsonAsync<GroupSummary>())!;
    private static AlarmDefinition Definition(string title="Shared reminder")=>new(title,DateTimeOffset.UtcNow.AddHours(2),DateTimeOffset.UtcNow,"UTC",0,0,null,null,0);
    private async Task Join(HttpClient owner,Guid groupId,SessionResponse member,HttpClient recipient)
    {
        var g=(await owner.GetFromJsonAsync<GroupDetails>("/api/groups/"+groupId))!.Group;
        var invite=(await(await owner.PostAsJsonAsync($"/api/groups/{groupId}/invitations",new GroupInviteRequest(member.User.Email,g.Revision))).Content.ReadFromJsonAsync<GroupInvitationResponse>())!;
        Assert.Equal(HttpStatusCode.NoContent,(await recipient.PostAsync($"/api/invitations/{invite.Id}/accept",null)).StatusCode);
    }
    [Fact]
    public async Task GroupMembersReceiveSharedAlarmButOnlyManagersCanPublish()
    {
        var(o,_)=await User();var(m,ms)=await User();var(x,_)=await User();using(o)using(m)using(x){
            var g=await Create(o);await Join(o,g.Id,ms,m);var id=Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK,(await o.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",new AlarmWriteRequest(0,Guid.NewGuid(),false,Definition()))).StatusCode);
            var owner=(await o.GetFromJsonAsync<GroupAlarmResponse[]>("/api/group-alarms"))!.Single(a=>a.Id==id);
            var member=(await m.GetFromJsonAsync<GroupAlarmResponse[]>("/api/group-alarms"))!.Single(a=>a.Id==id);
            Assert.True(owner.CanManage);Assert.False(member.CanManage);Assert.Equal(owner.Alarm,member.Alarm);
            Assert.DoesNotContain((await x.GetFromJsonAsync<GroupAlarmResponse[]>("/api/group-alarms"))!,a=>a.Id==id);
            Assert.Equal(HttpStatusCode.Forbidden,(await m.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",new AlarmWriteRequest(1,Guid.NewGuid(),false,Definition("Unauthorized")))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,(await x.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",new AlarmWriteRequest(1,Guid.NewGuid(),false,Definition("Outsider")))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,(await m.PutAsJsonAsync($"/api/alarms/{id}",new AlarmWriteRequest(0,Guid.NewGuid(),false,Definition("Personal override")))).StatusCode);
            Assert.DoesNotContain((await m.GetFromJsonAsync<PersonalAlarmChange[]>("/api/alarms"))!,a=>a.Id==id);
        }
    }
    [Fact]
    public async Task RetryIsIdempotentStaleEditConflictsAndDeletionReachesEveryMember()
    {
        var(o,_)=await User();var(m,ms)=await User();using(o)using(m){
            var g=await Create(o);await Join(o,g.Id,ms,m);var id=Guid.NewGuid();var request=new AlarmWriteRequest(0,Guid.NewGuid(),false,Definition());
            var first=await o.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",request);Assert.Equal(HttpStatusCode.OK,first.StatusCode);
            var retry=await o.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",request);Assert.Equal(1,(await retry.Content.ReadFromJsonAsync<GroupAlarmResponse>())!.Version);
            Assert.Equal(HttpStatusCode.OK,(await o.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",new AlarmWriteRequest(1,Guid.NewGuid(),false,Definition("Edited")))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,(await o.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",new AlarmWriteRequest(1,Guid.NewGuid(),false,Definition("Stale")))).StatusCode);
            Assert.Equal("Edited",(await m.GetFromJsonAsync<GroupAlarmResponse[]>("/api/group-alarms"))!.Single(a=>a.Id==id).Alarm.Title);
            Assert.Equal(HttpStatusCode.NoContent,(await o.DeleteAsync($"/api/groups/{g.Id}/alarms/{id}?version=2&mutationId={Guid.NewGuid()}")).StatusCode);
            Assert.DoesNotContain((await m.GetFromJsonAsync<GroupAlarmResponse[]>("/api/group-alarms"))!,a=>a.Id==id);
            var audit=(await o.GetFromJsonAsync<GroupAuditResponse[]>($"/api/groups/{g.Id}/audit"))!;
            Assert.Single(audit,a=>a.Action=="AlarmCreated");
        }
    }
    [Fact]
    public async Task LaterMembersReceiveExistingAlarmsAndLeavingRemovesAccess()
    {
        var(o,_)=await User();var(m,ms)=await User();using(o)using(m){
            var g=await Create(o);var id=Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK,(await o.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",new AlarmWriteRequest(0,Guid.NewGuid(),false,Definition()))).StatusCode);
            await Join(o,g.Id,ms,m);Assert.Contains((await m.GetFromJsonAsync<GroupAlarmResponse[]>("/api/group-alarms"))!,a=>a.Id==id);
            var rev=(await m.GetFromJsonAsync<GroupDetails>("/api/groups/"+g.Id))!.Group.Revision;
            Assert.Equal(HttpStatusCode.NoContent,(await m.PostAsJsonAsync($"/api/groups/{g.Id}/leave",new GroupRevisionRequest(rev))).StatusCode);
            Assert.DoesNotContain((await m.GetFromJsonAsync<GroupAlarmResponse[]>("/api/group-alarms"))!,a=>a.Id==id);
        }
    }
    [Fact]
    public async Task SuspensionHidesGroupAlarmsAndRoleChangeUpdatesEditingPermission()
    {
        var(o,_)=await User();var(m,ms)=await User();using(o)using(m){
            var g=await Create(o);await Join(o,g.Id,ms,m);var id=Guid.NewGuid();
            Assert.Equal(HttpStatusCode.OK,(await o.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",new AlarmWriteRequest(0,Guid.NewGuid(),false,Definition()))).StatusCode);
            var revision=(await o.GetFromJsonAsync<GroupDetails>("/api/groups/"+g.Id))!.Group.Revision;
            await o.PatchAsJsonAsync($"/api/groups/{g.Id}/members/{ms.User.Id}",new GroupRoleRequest(GroupRole.Administrator,revision));
            Assert.True((await m.GetFromJsonAsync<GroupAlarmResponse[]>("/api/group-alarms"))!.Single(a=>a.Id==id).CanManage);
            using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AuthDb>();var team=await db.Groups.SingleAsync(a=>a.Id==g.Id);team.IsActive=false;await db.SaveChangesAsync();
            Assert.DoesNotContain((await m.GetFromJsonAsync<GroupAlarmResponse[]>("/api/group-alarms"))!,a=>a.Id==id);
            Assert.Equal(HttpStatusCode.NotFound,(await o.PutAsJsonAsync($"/api/groups/{g.Id}/alarms/{id}",new AlarmWriteRequest(1,Guid.NewGuid(),false,Definition()))).StatusCode);
        }
    }
}
