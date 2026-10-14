using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
namespace TeamReminder.Server;
public sealed record PublicStatistics(long Accounts, long Groups, long Alarms, DateTimeOffset UpdatedAt);
public sealed record DesktopRelease(string Version, DateTimeOffset PublishedAt, string DownloadUrl);
public sealed class PublicPortal(IMemoryCache cache, IServiceScopeFactory scopes, IHttpClientFactory clients)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim releaseGate = new(1);
    public async Task<PublicStatistics> StatisticsAsync(CancellationToken ct)
    {
        if (cache.TryGetValue<PublicStatistics>("stats", out var cached)) return cached!;
        await gate.WaitAsync(ct);
        try {
            if (cache.TryGetValue<PublicStatistics>("stats", out cached)) return cached!;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
            var result = new PublicStatistics(await db.Accounts.LongCountAsync(x => x.DeletedAt == null, ct),
                await db.Groups.LongCountAsync(x => x.DeletedAt == null && x.IsActive, ct),
                await db.PersonalAlarms.LongCountAsync(x => !x.Deleted, ct), DateTimeOffset.UtcNow);
            cache.Set("stats", result, TimeSpan.FromSeconds(60));
            return result;
        } finally { gate.Release(); }
    }
    public async Task<DesktopRelease?> ReleaseAsync(CancellationToken ct)
    {
        if (cache.TryGetValue<ReleaseCache>("release", out var cached)) return cached!.Release;
        await releaseGate.WaitAsync(ct);
        try {
            if (cache.TryGetValue<ReleaseCache>("release", out cached)) return cached!.Release;
            using var response = await clients.CreateClient("releases").GetAsync("https://github.com/AndersonExner/Team-Reminder-Client/releases/latest/download/release.json", ct);
            DesktopRelease? release = null;
            if (response.StatusCode != HttpStatusCode.NotFound) {
                response.EnsureSuccessStatusCode();
                var data = await response.Content.ReadFromJsonAsync<GitHubRelease>(ct);
                var asset = data?.Assets.FirstOrDefault(x => x.Name.StartsWith("TeamReminder-Setup-", StringComparison.Ordinal)
                    && x.Name.EndsWith("-x64.exe", StringComparison.Ordinal)
                    && Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                    && uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/AndersonExner/Team-Reminder-Client/releases/download/", StringComparison.Ordinal));
                if (data is { Draft: false, Prerelease: false } && asset is not null) release = new(data.Tag, data.PublishedAt, asset.Url);
            }
            cache.Set("release", new ReleaseCache(release), TimeSpan.FromMinutes(5));
            return release;
        } finally { releaseGate.Release(); }
    }
    private sealed record ReleaseCache(DesktopRelease? Release);
    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt, bool Draft, bool Prerelease, GitHubAsset[] Assets);
    private sealed record GitHubAsset(string Name, [property: JsonPropertyName("browser_download_url")] string Url);
}
