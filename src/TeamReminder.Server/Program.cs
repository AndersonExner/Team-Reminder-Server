using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TeamReminder.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AuthDb>((services, options) => options.UseNpgsql(
    services.GetRequiredService<IConfiguration>().GetConnectionString("Database")
    ?? throw new InvalidOperationException("Configure ConnectionStrings__Database.")));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PublicPortal>();
builder.Services.AddHttpClient("releases", client => {
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TeamReminder-Server/1.0");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PasswordHasherOptions>(o => o.IterationCount = 210_000);
builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.AddSingleton<DummyPassword>();
builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthentication>("Session", _ => { });
builder.Services.AddAuthorization(o => o.AddPolicy("SystemAdmin", p => p.RequireAuthenticatedUser().RequireClaim("system_admin", "true")));
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("groups", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue<int?>("Groups:RequestsPerMinute") ?? 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue<int?>("Auth:RequestsPerMinute") ?? 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024);
var app = builder.Build();
var connection = app.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Configure ConnectionStrings__Database.");

if (args.Contains("--migrate"))
{
    await SchemaMigration.ApplyAsync(connection);
    return;
}

if (args.Contains("--bootstrap-admin"))
{
    await AdminBootstrap.RunAsync(app.Services);
    return;
}

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (AuthDb db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");
auth.MapPost("/register", async (RegisterRequest request, AuthDb db, IPasswordHasher<Account> hasher,
    TimeProvider clock, CancellationToken ct) =>
{
    var errors = RegistrationValidation.Validate(request);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    var email = request.Email!.Trim();
    var normalized = email.ToUpperInvariant();
    if (await db.Accounts.AnyAsync(x => x.NormalizedEmail == normalized, ct))
        return Results.Conflict(new { message = "Já existe uma conta com este e-mail." });
    var account = new Account { Name = request.Name!.Trim(), Email = email, NormalizedEmail = normalized,
        CreatedAt = clock.GetUtcNow() };
    account.PasswordHash = hasher.HashPassword(account, request.Password!);
    var session = new LoginSession { Account = account };
    var response = AuthTokens.Rotate(session, clock.GetUtcNow(), true);
    db.Accounts.Add(account);
    db.Sessions.Add(session);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    { return Results.Conflict(new { message = "Já existe uma conta com este e-mail." }); }
    return Results.Created("/api/auth/me", response);
});
auth.MapPost("/login", async (LoginRequest request, AuthDb db, IPasswordHasher<Account> hasher,
    DummyPassword dummy, TimeProvider clock, CancellationToken ct) =>
{
    if (!RegistrationValidation.ValidEmail(request.Email) || request.Password is null or { Length: 0 } or { Length: > 128 })
        return Results.Unauthorized();
    var normalized = request.Email!.Trim().ToUpperInvariant();
    var account = await db.Accounts.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
    var result = hasher.VerifyHashedPassword(account ?? dummy.Account, account?.PasswordHash ?? dummy.Hash, request.Password);
    if (account is null || !account.IsActive || account.DeletedAt != null || result == PasswordVerificationResult.Failed) return Results.Unauthorized();
    if (result == PasswordVerificationResult.SuccessRehashNeeded)
        account.PasswordHash = hasher.HashPassword(account, request.Password);
    var session = new LoginSession { Account = account };
    var response = AuthTokens.Rotate(session, clock.GetUtcNow(), true);
    db.Sessions.Add(session);
    await db.SaveChangesAsync(ct);
    return Results.Ok(response);
});
auth.MapPost("/refresh", async (RefreshRequest request, AuthDb db, TimeProvider clock, CancellationToken ct) =>
{
    if (request.RefreshToken is not { Length: 64 }) return Results.Unauthorized();
    var hash = AuthTokens.Hash(request.RefreshToken);
    var now = clock.GetUtcNow();
    var session = await db.Sessions.Include(x => x.Account).SingleOrDefaultAsync(
        x => x.Account.IsActive && x.Account.DeletedAt == null && x.AccountVersion == x.Account.SessionVersion && x.RefreshHash == hash && x.RefreshExpiresAt > now, ct);
    if (session is null) return Results.Unauthorized();
    var response = AuthTokens.Rotate(session, now, false);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateConcurrencyException) { return Results.Unauthorized(); }
    return Results.Ok(response);
});
auth.MapGet("/me", async (ClaimsPrincipal principal, AuthDb db, CancellationToken ct) =>
{
    var id = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    return Results.Ok(AuthTokens.User(await db.Accounts.SingleAsync(x => x.Id == id, ct)));
}).RequireAuthorization();
auth.MapPost("/logout", async (ClaimsPrincipal principal, AuthDb db, CancellationToken ct) =>
{
    var id = Guid.Parse(principal.FindFirstValue("session_id")!);
    await db.Sessions.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/public/statistics", async (PublicPortal portal, CancellationToken ct) =>
    Results.Ok(await portal.StatisticsAsync(ct)));
app.MapGet("/api/public/release", async (PublicPortal portal, CancellationToken ct) => {
    var release = await portal.ReleaseAsync(ct);
    return release is null ? Results.NotFound(new { message = "Nenhum instalador publicado." }) : Results.Ok(release);
});
app.MapGet("/download/windows", async (PublicPortal portal, CancellationToken ct) => {
    var release = await portal.ReleaseAsync(ct);
    return release is null ? Results.NotFound(new { message = "Download em breve." }) : Results.Redirect(release.DownloadUrl);
});
app.MapAdministration();
app.MapPersonalAlarms();
app.MapGroups();
app.Run();

public partial class Program { }

namespace TeamReminder.Server
{
    public sealed class DummyPassword
    {
        public Account Account { get; } = new();
        public string Hash { get; }
        public DummyPassword(IPasswordHasher<Account> hasher) => Hash = hasher.HashPassword(Account, Guid.NewGuid().ToString());
    }

    public static class RegistrationValidation
    {
        public static bool ValidEmail(string? email) => email is not null && email.Trim().Length is > 0 and <= 254
            && new EmailAddressAttribute().IsValid(email.Trim());
        public static Dictionary<string, string[]> Validate(RegisterRequest request)
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
                errors["name"] = ["Informe um nome de até 100 caracteres."];
            if (!ValidEmail(request.Email)) errors["email"] = ["Informe um e-mail válido."];
            if (request.Password is null || request.Password.Length is < 10 or > 128 || string.IsNullOrWhiteSpace(request.Password))
                errors["password"] = ["Use uma senha de 10 a 128 caracteres."];
            return errors;
        }
    }
}
