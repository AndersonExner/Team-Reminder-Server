using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamReminder.Server;

namespace TeamReminder.Server.Tests;

public sealed class LimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static AlarmDefinition Definition() =>
        new("Limit", DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow, "UTC", 0, 0, null, null, 0);

    private async Task<(HttpClient Client, SessionResponse Session)> User()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Limit", $"limit-{Guid.NewGuid():N}@example.com", "strong test password"));
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
        return (client, session);
    }

    private async Task SeedPersonalAlarms(Guid owner, int count, bool deleted)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
        var account = await db.Accounts.SingleAsync(x => x.Id == owner);
        for (var i = 0; i < count; i++)
            db.PersonalAlarms.Add(new PersonalAlarm
            {
                AccountId = owner, Id = Guid.NewGuid(), Version = ++account.AlarmRevision, MutationId = Guid.NewGuid(),
                Deleted = deleted, Title = "Seed", ScheduledAtUtc = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DeletedPersonalAlarmsDoNotCountTowardActiveLimit()
    {
        var (client, session) = await User();
        using (client)
        {
            await SeedPersonalAlarms(session.User.Id, 1000, deleted: true);
            var created = await client.PutAsJsonAsync($"/api/alarms/{Guid.NewGuid()}", new AlarmWriteRequest(0, Guid.NewGuid(), false, Definition()));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            await SeedPersonalAlarms(session.User.Id, 999, deleted: false);
            var blocked = await client.PutAsJsonAsync($"/api/alarms/{Guid.NewGuid()}", new AlarmWriteRequest(0, Guid.NewGuid(), false, Definition()));
            Assert.Equal((HttpStatusCode)422, blocked.StatusCode);

            // Restoring a tombstone would also exceed the active limit.
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
            var tombstone = await db.PersonalAlarms.FirstAsync(x => x.AccountId == session.User.Id && x.Deleted);
            var restored = await client.PutAsJsonAsync($"/api/alarms/{tombstone.Id}",
                new AlarmWriteRequest(tombstone.Version, Guid.NewGuid(), false, Definition()));
            Assert.Equal((HttpStatusCode)422, restored.StatusCode);
        }
    }

    [Fact]
    public async Task DeletedGroupAlarmsDoNotCountTowardActiveLimit()
    {
        var (client, session) = await User();
        using (client)
        {
            var group = (await (await client.PostAsJsonAsync("/api/groups", new GroupCreateRequest("Limit", ""))).Content.ReadFromJsonAsync<GroupSummary>())!;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
                for (var i = 0; i < 100; i++)
                    db.GroupAlarms.Add(new GroupAlarm
                    {
                        Id = Guid.NewGuid(), GroupId = group.Id, CreatedByAccountId = session.User.Id, Version = 2,
                        MutationId = Guid.NewGuid(), Deleted = true, DefinitionJson = JsonSerializer.Serialize(Definition())
                    });
                await db.SaveChangesAsync();
            }
            var created = await client.PutAsJsonAsync($"/api/groups/{group.Id}/alarms/{Guid.NewGuid()}",
                new AlarmWriteRequest(0, Guid.NewGuid(), false, Definition()));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }
    }

    [Fact]
    public async Task AuthRateLimitUsesForwardedClientAddress()
    {
        using var proxied = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:Enabled"] = "true",
                ["Auth:RequestsPerMinute"] = "2"
            })));
        using var client = proxied.CreateClient();
        // Unique addresses keep this test independent from other fixed-window partitions.
        var first = $"203.0.113.{Random.Shared.Next(1, 255)}";
        var second = $"198.51.100.{Random.Shared.Next(1, 255)}";
        async Task<HttpStatusCode> Attempt(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register") { Content = JsonContent.Create(new RegisterRequest(null, null, null)) };
            request.Headers.Add("X-Forwarded-For", address);
            return (await client.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await Attempt(first));
        Assert.Equal(HttpStatusCode.BadRequest, await Attempt(first));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Attempt(first));
        Assert.Equal(HttpStatusCode.BadRequest, await Attempt(second));
    }
}
