using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TeamReminder.Server;
namespace TeamReminder.Server.Tests;

[CollectionDefinition("Administration", DisableParallelization=true)]
public sealed class AdminCollection { }
[Collection("Administration")]
public sealed class AdminTests(ApiFactory factory): IClassFixture<ApiFactory>
{
    private async Task<(HttpClient Client,SessionResponse Session)> Login(bool admin=false)
    {
        var client=factory.CreateClient();
        var response=await client.PostAsJsonAsync("/api/auth/register",new RegisterRequest("Admin tests","admin-"+Guid.NewGuid()+"@example.com","strong test password"));
        response.EnsureSuccessStatusCode();
        var session=(await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        if(admin){using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AuthDb>();var a=await db.Accounts.SingleAsync(x=>x.Id==session.User.Id);a.IsSystemAdmin=true;await db.SaveChangesAsync();}
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.AccessToken);
        return(client,session);
    }
    private async Task<AdminUser> Create(HttpClient admin,bool systemAdmin=false)
    {
        var response=await admin.PostAsJsonAsync("/api/admin/users",new AdminUserRequest("Managed","managed-"+Guid.NewGuid()+"@example.com","strong test password",true,systemAdmin));
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);return (await response.Content.ReadFromJsonAsync<AdminUser>())!;
    }
    [Fact]
    public async Task AnonymousAndGroupOwnerHaveNoAdministrativeAccess()
    {
        using var anonymous=factory.CreateClient();
        var loginPage=await anonymous.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK,loginPage.StatusCode);
        Assert.Contains("Acesso administrativo",await loginPage.Content.ReadAsStringAsync());
        Assert.Contains("frame-ancestors 'none'",loginPage.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/admin/users")).StatusCode);
        var (user,s)=await Login();using(user){
            Assert.Equal(HttpStatusCode.Forbidden,(await user.GetAsync("/api/admin/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,(await user.PostAsJsonAsync("/api/admin/users",new AdminUserRequest("Injection","x@example.com","strong test password",true,true))).StatusCode);
        }
    }
    [Fact]
    public async Task SearchIgnoresCaseAndTreatsWildcardsLiterally()
    {
        var (admin,_)=await Login(true);var (user,s)=await Login();using(admin)using(user){
            var found=(await admin.GetFromJsonAsync<AdminPage<AdminUser>>("/api/admin/users?q="+Uri.EscapeDataString(s.User.Email.ToUpperInvariant())))!;
            Assert.Equal(s.User.Id,Assert.Single(found.Items).Id);
            var wildcard=(await admin.GetFromJsonAsync<AdminPage<AdminUser>>("/api/admin/users?q="+Uri.EscapeDataString("%_%")))!;
            Assert.DoesNotContain(wildcard.Items,x=>x.Id==s.User.Id);
        }
    }
    [Fact]
    public async Task DisabledAccountLosesAccessRefreshAndLoginAndOldSessionCannotRevive()
    {
        var (admin,_)=await Login(true);var (user,s)=await Login();using(admin)using(user){
            var a=(await admin.GetFromJsonAsync<AdminPage<AdminUser>>("/api/admin/users?q="+s.User.Email))!.Items.Single();
            var disable=await admin.PatchAsJsonAsync("/api/admin/users/"+a.Id,new AdminUserRequest(a.Name,a.Email,null,false,false,a.Revision));
            Assert.Equal(HttpStatusCode.OK,disable.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,(await user.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,(await user.PostAsJsonAsync("/api/auth/refresh",new RefreshRequest(s.RefreshToken))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,(await user.PostAsJsonAsync("/api/auth/login",new LoginRequest(a.Email,"strong test password"))).StatusCode);
            a=(await disable.Content.ReadFromJsonAsync<AdminUser>())!;
            Assert.Equal(HttpStatusCode.OK,(await admin.PatchAsJsonAsync("/api/admin/users/"+a.Id,new AdminUserRequest(a.Name,a.Email,null,true,false,a.Revision))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,(await user.GetAsync("/api/auth/me")).StatusCode);
        }
    }
    [Fact]
    public async Task CrudRejectsStaleChangesAndPreservesDeletionForRestore()
    {
        var (admin,_)=await Login(true);using(admin){
            var a=await Create(admin);
            var update=new AdminUserRequest("Edited",a.Email,null,true,false,a.Revision);
            var response=await admin.PatchAsJsonAsync("/api/admin/users/"+a.Id,update);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,(await admin.PatchAsJsonAsync("/api/admin/users/"+a.Id,update)).StatusCode);
            a=(await response.Content.ReadFromJsonAsync<AdminUser>())!;
            Assert.Equal(HttpStatusCode.NoContent,(await admin.DeleteAsync("/api/admin/users/"+a.Id+"?revision="+a.Revision)).StatusCode);
            a=(await admin.GetFromJsonAsync<AdminPage<AdminUser>>("/api/admin/users?status=deleted&q="+a.Email))!.Items.Single();
            Assert.NotNull(a.DeletedAt);
            response=await admin.PostAsJsonAsync("/api/admin/users/"+a.Id+"/restore",new AdminRevision(a.Revision));
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);a=(await response.Content.ReadFromJsonAsync<AdminUser>())!;
            Assert.Null(a.DeletedAt);Assert.False(a.IsActive);
            var audit=await admin.GetFromJsonAsync<AdminPage<AdminAuditEvent>>("/api/admin/audit");
            Assert.Contains(audit!.Items,x=>x.TargetId==a.Id&&x.Action=="Restored");
            Assert.DoesNotContain("password",await (await admin.GetAsync("/api/admin/users")).Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
        }
    }
    [Fact]
    public async Task OwnAdministrativeAccessCannotBeRemoved()
    {
        var (admin,s)=await Login(true);using(admin){
            var a=(await admin.GetFromJsonAsync<AdminUser>("/api/admin/me"))!;
            Assert.Equal(HttpStatusCode.Conflict,(await admin.PatchAsJsonAsync("/api/admin/users/"+a.Id,new AdminUserRequest(a.Name,a.Email,null,false,true,a.Revision))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,(await admin.DeleteAsync("/api/admin/users/"+a.Id+"?revision="+a.Revision)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,(await admin.PostAsJsonAsync("/api/admin/users/"+a.Id+"/restore",new AdminRevision(a.Revision))).StatusCode);
        }
    }
    [Fact]
    public async Task SuspendedGroupsDisappearFromAppAndCanBeRestoredByAdmin()
    {
        var (admin,_)=await Login(true);var (owner,s)=await Login();using(admin)using(owner){
            var response=await admin.PostAsJsonAsync("/api/admin/groups",new AdminGroupRequest("Managed group","description",s.User.Id,true));
            Assert.Equal(HttpStatusCode.Created,response.StatusCode);var g=(await response.Content.ReadFromJsonAsync<AdminGroup>())!;
            Assert.Equal(HttpStatusCode.OK,(await owner.GetAsync("/api/groups/"+g.Id)).StatusCode);
            response=await admin.PatchAsJsonAsync("/api/admin/groups/"+g.Id,new AdminGroupRequest(g.Name,g.Description,g.OwnerAccountId,false,g.Revision));
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);g=(await response.Content.ReadFromJsonAsync<AdminGroup>())!;
            Assert.Equal(HttpStatusCode.NotFound,(await owner.GetAsync("/api/groups/"+g.Id)).StatusCode);
            Assert.DoesNotContain((await owner.GetFromJsonAsync<GroupSummary[]>("/api/groups"))!,x=>x.Id==g.Id);
            Assert.Equal(HttpStatusCode.NoContent,(await admin.DeleteAsync("/api/admin/groups/"+g.Id+"?revision="+g.Revision)).StatusCode);
            g=(await admin.GetFromJsonAsync<AdminPage<AdminGroup>>("/api/admin/groups?status=deleted&q=Managed"))!.Items.Single(x=>x.Id==g.Id);
            response=await admin.PostAsJsonAsync("/api/admin/groups/"+g.Id+"/restore",new AdminRevision(g.Revision));Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            g=(await response.Content.ReadFromJsonAsync<AdminGroup>())!;Assert.False(g.IsActive);Assert.Null(g.DeletedAt);
        }
    }
    [Fact]
    public async Task OwnerCannotBeDeletedBeforeTransferringTheirGroups()
    {
        var (admin,_)=await Login(true);using(admin){
            var owner=await Create(admin);
            var response=await admin.PostAsJsonAsync("/api/admin/groups",new AdminGroupRequest("Ownership","",owner.Id,true));
            Assert.Equal(HttpStatusCode.Created,response.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,(await admin.DeleteAsync("/api/admin/users/"+owner.Id+"?revision="+owner.Revision)).StatusCode);
        }
    }
}
