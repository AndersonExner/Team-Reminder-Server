using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TeamReminder.Server;

namespace TeamReminder.Server.Tests;

public sealed class PersonalAlarmTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<HttpClient> Client()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("User", $"sync-{Guid.NewGuid():N}@example.com", "strong test password"));
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return client;
    }
    private static AlarmDefinition Definition(string title = "Reunião") => new(title, DateTimeOffset.UtcNow.AddHours(1),
        DateTimeOffset.UtcNow, "UTC", 0, 0, "https://example.com/meeting", "Abrir reunião", 1);
    private static AlarmWriteRequest Write(long version = 0, AlarmDefinition? definition = null) => new(version, Guid.NewGuid(), false, definition ?? Definition());
    private static async Task<PersonalAlarmChange> Put(HttpClient client, Guid id, AlarmWriteRequest request)
    {
        var response = await client.PutAsJsonAsync($"/api/alarms/{id}", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PersonalAlarmChange>())!;
    }

    [Fact]
    public async Task CrudAndDeletionAreVisibleThroughIncrementalSync()
    {
        using var client = await Client();
        var id = Guid.NewGuid();
        var first = await Put(client, id, Write());
        Assert.Equal(1, first.Version);
        Assert.Equal(first, await client.GetFromJsonAsync<PersonalAlarmChange>($"/api/alarms/{id}"));
        var second = await Put(client, id, Write(first.Version, Definition("Atualizada")));
        Assert.Equal(2, second.Version);
        var changes = (await client.GetFromJsonAsync<AlarmChangeSet>("/api/sync?since=1"))!;
        Assert.Equal(2, changes.Cursor);
        Assert.Equal(second, Assert.Single(changes.Changes));
        var deletionId = Guid.NewGuid();
        var deletion = await client.DeleteAsync($"/api/alarms/{id}?version={second.Version}&mutationId={deletionId}");
        Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/alarms/{id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PersonalAlarmChange[]>("/api/alarms"))!);
        var deleted = (await client.GetFromJsonAsync<AlarmChangeSet>("/api/sync?since=2"))!;
        Assert.True(Assert.Single(deleted.Changes).Deleted);
        Assert.Equal(3, deleted.Cursor);
        Assert.Empty((await client.GetFromJsonAsync<AlarmChangeSet>("/api/sync?since=3"))!.Changes);
    }

    [Fact]
    public async Task RepeatingMutationDoesNotDuplicateOrAdvanceCursor()
    {
        using var client = await Client();
        var id = Guid.NewGuid(); var write = Write();
        var first = await Put(client, id, write);
        Assert.Equal(first, await Put(client, id, write));
        Assert.Equal(1, (await client.GetFromJsonAsync<AlarmChangeSet>("/api/sync"))!.Cursor);
    }

    [Fact]
    public async Task StaleWriteReturnsCurrentVersionWithoutOverwriting()
    {
        using var client = await Client(); var id = Guid.NewGuid();
        await Put(client, id, Write());
        var current = await Put(client, id, Write(1, Definition("Servidor")));
        var stale = await client.PutAsJsonAsync($"/api/alarms/{id}", Write(1, Definition("Offline")));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetProperty("current").GetProperty("version").GetInt64());
        Assert.Equal(current, await client.GetFromJsonAsync<PersonalAlarmChange>($"/api/alarms/{id}"));
    }

    [Fact]
    public async Task AccountsCannotReadOrModifyEachOthersAlarms()
    {
        using var first = await Client(); using var second = await Client();
        var id = Guid.NewGuid(); var alarm = await Put(first, id, Write());
        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync($"/api/alarms/{id}")).StatusCode);
        Assert.Empty((await second.GetFromJsonAsync<AlarmChangeSet>("/api/sync"))!.Changes);
        var deletion = await second.DeleteAsync($"/api/alarms/{id}?version=1&mutationId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Conflict, deletion.StatusCode);
        await Put(second, id, Write(0, Definition("Outra conta")));
        Assert.Equal(alarm, await first.GetFromJsonAsync<PersonalAlarmChange>($"/api/alarms/{id}"));
    }

    [Fact]
    public async Task ConcurrentWritesGetOrderedUniqueRevisions()
    {
        using var client = await Client();
        var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Put(client, Guid.NewGuid(), Write())));
        Assert.Equal(Enumerable.Range(1, 8).Select(x => (long)x), writes.Select(x => x.Version).Order());
        var sync = (await client.GetFromJsonAsync<AlarmChangeSet>("/api/sync"))!;
        Assert.Equal(8, sync.Cursor);
        Assert.Equal(8, sync.Changes.Count);
    }

    [Fact]
    public async Task DeletedAlarmCanBeRestoredOnlyWithCurrentVersion()
    {
        using var client = await Client(); var id = Guid.NewGuid();
        await Put(client, id, Write());
        var deleted = await Put(client, id, new(1, Guid.NewGuid(), true, null));
        Assert.True(deleted.Deleted);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/alarms/{id}", Write(1))).StatusCode);
        Assert.False((await Put(client, id, Write(2))).Deleted);
    }

    [Fact]
    public async Task ValidationRejectsInvalidScheduleRecurrenceLinksAndTokens()
    {
        using var client = await Client();
        foreach (var definition in new[] {
            Definition() with { Title = "" }, Definition() with { TimeZoneId = "unknown-zone" },
            Definition() with { Recurrence = 2, Days = 0 }, Definition() with { SoundKind = 5 },
            Definition() with { LinkUrl = "https://user:password@example.com" },
            Definition() with { ScheduledAtUtc = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(3)) } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/alarms/{Guid.NewGuid()}", Write(definition: definition))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/sync?since=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/sync?since=100")).StatusCode);
    }

    [Fact]
    public async Task EveryEndpointRequiresAuthentication()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/alarms")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sync")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/alarms/{Guid.NewGuid()}", Write())).StatusCode);
    }
}
