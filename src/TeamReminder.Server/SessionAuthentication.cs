using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace TeamReminder.Server;

public sealed class SessionAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AuthDb db, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..];
        if (token.Length != 64) return AuthenticateResult.Fail("Sessão inválida.");
        var hash = AuthTokens.Hash(token);
        var now = clock.GetUtcNow();
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(
            x => x.AccessHash == hash && x.AccessExpiresAt > now && x.RefreshExpiresAt > now, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Sessão expirada ou inválida.");
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, session.AccountId.ToString()),
            new Claim("session_id", session.Id.ToString())], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
