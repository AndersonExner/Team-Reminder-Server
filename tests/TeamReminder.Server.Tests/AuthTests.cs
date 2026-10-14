using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamReminder.Server;

namespace TeamReminder.Server.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string connection = Environment.GetEnvironmentVariable("TEAMREMINDER_TEST_DATABASE")
        ?? throw new InvalidOperationException("Configure TEAMREMINDER_TEST_DATABASE com um PostgreSQL exclusivo para testes.");
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        SchemaMigration.ApplyAsync(connection).GetAwaiter().GetResult();
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Database"] = connection, ["Auth:RequestsPerMinute"] = "1000" }));
    }
}

public sealed class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "a strong test password";
    private static string Email() => $"user-{Guid.NewGuid():N}@example.com";
    private async Task<SessionResponse> Register(HttpClient client, string? email = null)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("Anderson", email ?? Email(), Password));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
    }
    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Fact]
    public async Task RegisterPersistsHashAndAuthenticatedProfile()
    {
        using var client = factory.CreateClient();
        var session = await Register(client);
        Authorize(client, session.AccessToken);
        var profile = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");
        Assert.Equal(session.User, profile);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
        var account = await db.Accounts.FindAsync(session.User.Id);
        Assert.NotEqual(Password, account!.PasswordHash);
        var stored = await db.Sessions.SingleAsync(x => x.AccountId == session.User.Id);
        Assert.Equal(AuthTokens.Hash(session.AccessToken), stored.AccessHash);
        Assert.NotEqual(session.RefreshToken, stored.RefreshHash);
    }

    [Fact]
    public async Task DuplicateEmailIsCaseInsensitive()
    {
        using var client = factory.CreateClient();
        var email = Email();
        await Register(client, email);
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("Other", " " + email.ToUpperInvariant() + " ", Password));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("", "test@example.com", Password)]
    [InlineData("Name", "invalid", Password)]
    [InlineData("Name", "test@example.com", "short")]
    [InlineData(null, null, null)]
    public async Task InvalidRegistrationReturnsValidationErrors(string? name, string? email, string? password)
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, email, password));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoginAcceptsNormalizedEmailAndRejectsWrongPassword()
    {
        using var client = factory.CreateClient();
        var email = Email();
        var first = await Register(client, email);
        var valid = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email.ToUpperInvariant(), Password));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var second = (await valid.Content.ReadFromJsonAsync<SessionResponse>())!;
        Assert.Equal(first.User.Id, second.User.Id);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        var invalid = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "incorrect password"));
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task RefreshRotatesTokensAndPreventsReuse()
    {
        using var client = factory.CreateClient();
        var first = await Register(client);
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(first.RefreshExpiresAt, second.RefreshExpiresAt);
        Authorize(client, first.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Authorize(client, second.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task ConcurrentRefreshHasExactlyOneWinner()
    {
        using var client = factory.CreateClient();
        var session = await Register(client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken))));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredAccessCanRefreshButExpiredSessionCannot()
    {
        using var client = factory.CreateClient();
        var session = await Register(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
        await db.Sessions.Where(x => x.AccountId == session.User.Id).ExecuteUpdateAsync(
            setters => setters.SetProperty(x => x.AccessExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Authorize(client, session.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var renewed = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        await db.Sessions.Where(x => x.AccountId == session.User.Id).ExecuteUpdateAsync(
            setters => setters.SetProperty(x => x.RefreshExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(renewed.RefreshToken))).StatusCode);
    }

    [Fact]
    public async Task LogoutRevokesOnlyCurrentSession()
    {
        using var client = factory.CreateClient();
        var email = Email();
        var first = await Register(client, email);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        var other = (await login.Content.ReadFromJsonAsync<SessionResponse>())!;
        Authorize(client, first.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken))).StatusCode);
        Authorize(client, other.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ProfileRequiresAuthentication()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Authorize(client, new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
