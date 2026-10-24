using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
namespace TeamReminder.Server;
public sealed record PublicStatistics(long Accounts, long Groups, long Alarms, DateTimeOffset UpdatedAt);
public sealed record DesktopRelease(string Version, DateTimeOffset PublishedAt, string DownloadUrl, string? Sha256 = null);
public sealed class PublicPortal(IMemoryCache cache, IServiceScopeFactory scopes, IHttpClientFactory clients, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim releaseGate = new(1);
    private DesktopRelease? lastKnownRelease;
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
                await db.PersonalAlarms.LongCountAsync(x => !x.Deleted, ct) + await db.GroupAlarms.LongCountAsync(x=>!x.Deleted&&db.Groups.Any(g=>g.Id==x.GroupId&&g.DeletedAt==null),ct), clock.GetUtcNow());
            cache.Set("stats", result, TimeSpan.FromSeconds(60));
            return result;
        } finally { gate.Release(); }
    }
    public async Task<DesktopRelease?> ReleaseAsync(CancellationToken ct)
    {
        if (cache.TryGetValue<ReleaseCache>("release", out var cached)) return cached!.Result();
        await releaseGate.WaitAsync(ct);
        try {
            if (cache.TryGetValue<ReleaseCache>("release", out cached)) return cached!.Result();
            try { return await FetchReleaseAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                // Avoid calling GitHub on every request while it fails; serve the last known release if any.
                var failure = new ReleaseCache(lastKnownRelease, lastKnownRelease is null);
                cache.Set("release", failure, TimeSpan.FromMinutes(1));
                return failure.Result();
            }
        } finally { releaseGate.Release(); }
    }
    private async Task<DesktopRelease?> FetchReleaseAsync(CancellationToken ct)
    {
            using var response = await clients.CreateClient("releases").GetAsync("https://github.com/AndersonExner/Team-Reminder-Client/releases/latest/download/release.json", ct);
            DesktopRelease? release = null;
            if (response.StatusCode != HttpStatusCode.NotFound) {
                response.EnsureSuccessStatusCode();
                var data = await response.Content.ReadFromJsonAsync<GitHubRelease>(ct);
                var asset = data?.Assets.FirstOrDefault(x => x.Name.StartsWith("TeamReminder-Setup-", StringComparison.Ordinal)
                    && x.Name.EndsWith("-x64.exe", StringComparison.Ordinal)
                    && Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                    && uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/AndersonExner/Team-Reminder-Client/releases/download/", StringComparison.Ordinal));
                // The checksum is shown on the download page only when it is a well-formed SHA-256.
                if (data is { Draft: false, Prerelease: false } && asset is not null)
                    release = new(data.Tag, data.PublishedAt, asset.Url,
                        asset.Sha256 is { Length: 64 } sha && sha.All(Uri.IsHexDigit) ? sha.ToLowerInvariant() : null);
            }
            cache.Set("release", new ReleaseCache(release, false), TimeSpan.FromMinutes(5));
            lastKnownRelease = release;
            return release;
    }
    private sealed record ReleaseCache(DesktopRelease? Release, bool Failed)
    {
        public DesktopRelease? Result() => Failed ? throw new HttpRequestException("Release indisponível no GitHub.") : Release;
    }
    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt, bool Draft, bool Prerelease, GitHubAsset[] Assets);
    private sealed record GitHubAsset(string Name, [property: JsonPropertyName("browser_download_url")] string Url, string? Sha256 = null);
}
