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
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://api.github.com/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(payload) });
        }
    }
    private static PublicPortal Portal(Handler handler) => new(new MemoryCache(new MemoryCacheOptions()),
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), handler);
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
    [Fact]
    public async Task UpstreamFailureDoesNotBecomeAMissingRelease()
    {
        using var handler = new Handler(HttpStatusCode.Forbidden, null);
        await Assert.ThrowsAsync<HttpRequestException>(() => Portal(handler).ReleaseAsync(default));
    }
}
