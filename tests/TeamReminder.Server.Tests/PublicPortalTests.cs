using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using TeamReminder.Server;
namespace TeamReminder.Server.Tests;

public sealed class PublicPortalTests
{
    private sealed class Handler(HttpStatusCode status, object? payload) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpStatusCode Status { get; set; } = status;
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://api.github.com/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = JsonContent.Create(payload) });
        }
    }
    private static PublicPortal Portal(Handler handler, IMemoryCache? cache = null) => new(cache ?? new MemoryCache(new MemoryCacheOptions()),
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), handler, TimeProvider.System);
    [Fact]
    public async Task MissingReleaseIsCachedAndDoesNotInventADownload()
    {
        using var handler = new Handler(HttpStatusCode.NotFound, null);
        var portal = Portal(handler);
        Assert.Null(await portal.ReleaseAsync(default));
        Assert.Null(await portal.ReleaseAsync(default));
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("https://github.com/AndersonExner/Team-Reminder-Client/releases/download/v0.2.0/TeamReminder-Setup-0.2.0-x64.exe", false, true)]
    [InlineData("https://example.com/setup.exe", false, false)]
    [InlineData("https://github.com/AndersonExner/Team-Reminder-Client/releases/download/v0.2.0/setup.exe", true, false)]
    public async Task OnlyStableInstallerFromOurRepositoryIsAccepted(string url, bool prerelease, bool expected)
    {
        using var handler = new Handler(HttpStatusCode.OK, new {
            tag_name = "v0.2.0", published_at = DateTimeOffset.UtcNow, draft = false, prerelease,
            assets = new[] { new { name = "TeamReminder-Setup-0.2.0-x64.exe", browser_download_url = url } }
        });
        var result = await Portal(handler).ReleaseAsync(default);
        Assert.Equal(expected, result is not null);
        if (expected) { Assert.Equal("v0.2.0", result!.Version); Assert.Equal(url, result.DownloadUrl); }
    }
    [Theory]
    [InlineData("CDBCCEC987541E8A801860992DE3C98759E488929901A5586B8644886C9D04B4", "cdbccec987541e8a801860992de3c98759e488929901a5586b8644886c9d04b4")]
    [InlineData("not-a-checksum", null)]
    [InlineData(null, null)]
    public async Task ReleaseExposesOnlyWellFormedChecksum(string? published, string? expected)
    {
        using var handler = new Handler(HttpStatusCode.OK, new {
            tag_name = "v0.4.0", published_at = DateTimeOffset.UtcNow, draft = false, prerelease = false,
            assets = new[] { new { name = "TeamReminder-Setup-0.4.0-x64.exe", sha256 = published,
                browser_download_url = "https://github.com/AndersonExner/Team-Reminder-Client/releases/download/v0.4.0/TeamReminder-Setup-0.4.0-x64.exe" } }
        });
        Assert.Equal(expected, (await Portal(handler).ReleaseAsync(default))!.Sha256);
    }
    [Fact]
    public async Task UpstreamFailureDoesNotBecomeAMissingRelease()
    {
        using var handler = new Handler(HttpStatusCode.Forbidden, null);
        await Assert.ThrowsAsync<HttpRequestException>(() => Portal(handler).ReleaseAsync(default));
    }
    [Fact]
    public async Task UpstreamFailureIsCachedBrieflyAndServesLastKnownRelease()
    {
        using var failing = new Handler(HttpStatusCode.Forbidden, null);
        var portal = Portal(failing);
        await Assert.ThrowsAsync<HttpRequestException>(() => portal.ReleaseAsync(default));
        await Assert.ThrowsAsync<HttpRequestException>(() => portal.ReleaseAsync(default));
        Assert.Equal(1, failing.Calls);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var handler = new Handler(HttpStatusCode.OK, new {
            tag_name = "v0.2.0", published_at = DateTimeOffset.UtcNow, draft = false, prerelease = false,
            assets = new[] { new { name = "TeamReminder-Setup-0.2.0-x64.exe",
                browser_download_url = "https://github.com/AndersonExner/Team-Reminder-Client/releases/download/v0.2.0/TeamReminder-Setup-0.2.0-x64.exe" } }
        });
        var known = Portal(handler, cache);
        Assert.NotNull(await known.ReleaseAsync(default));
        cache.Remove("release");
        handler.Status = HttpStatusCode.BadGateway;
        Assert.Equal("v0.2.0", (await known.ReleaseAsync(default))!.Version);
    }
}
