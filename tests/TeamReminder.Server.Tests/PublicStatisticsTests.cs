using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TeamReminder.Server;
namespace TeamReminder.Server.Tests;
[CollectionDefinition("Public portal", DisableParallelization = true)]
public sealed class PublicPortalCollection { }
[Collection("Public portal")]
public sealed class PublicStatisticsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task AnonymousStatisticsMatchOnlyNonDeletedRowsAndExposeNoPersonalData()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
        var account = new Account { Name = "Private name", Email = "portal-" + Guid.NewGuid() + "@example.com", NormalizedEmail = Guid.NewGuid().ToString(), PasswordHash = "private" };
        db.Accounts.Add(account);
        db.Groups.AddRange(new TeamGroup { OwnerAccountId = account.Id, Name = "Active", CreatedAt = DateTimeOffset.UtcNow },
            new TeamGroup { OwnerAccountId = account.Id, Name = "Removed", DeletedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
        db.PersonalAlarms.AddRange(new PersonalAlarm { Id = Guid.NewGuid(), AccountId = account.Id, Version = 1 },
            new PersonalAlarm { Id = Guid.NewGuid(), AccountId = account.Id, Version = 2, Deleted = true });
        await db.SaveChangesAsync();
        factory.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>().Remove("stats");
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/public/statistics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(account.Email, json); Assert.DoesNotContain(account.Name, json);
        var totals = (await response.Content.ReadFromJsonAsync<PublicStatistics>())!;
        Assert.Equal(await db.Accounts.LongCountAsync(x=>x.DeletedAt==null), totals.Accounts);
        Assert.Equal(await db.Groups.LongCountAsync(x => x.DeletedAt == null && x.IsActive), totals.Groups);
        Assert.Equal(await db.PersonalAlarms.LongCountAsync(x => !x.Deleted), totals.Alarms);
        var page = await client.GetStringAsync("/");
        Assert.Contains("Baixar para Windows", page);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/portal.js")).StatusCode);
    }
}
