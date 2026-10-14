using System.Security.Cryptography;
using System.Text;

namespace TeamReminder.Server;

public sealed record UserResponse(Guid Id, string Name, string Email);
public sealed record SessionResponse(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt, UserResponse User);
public sealed record RegisterRequest(string? Name, string? Email, string? Password);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record RefreshRequest(string? RefreshToken);

public static class AuthTokens
{
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static UserResponse User(Account account) => new(account.Id, account.Name, account.Email);

    public static SessionResponse Rotate(LoginSession session, DateTimeOffset now, bool newSession)
    {
        // PostgreSQL persists timestamps with microsecond precision.
        now = new DateTimeOffset(now.Ticks - now.Ticks % 10, now.Offset);
        var access = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var refresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        session.AccessHash = Hash(access);
        session.RefreshHash = Hash(refresh);
        session.AccessExpiresAt = now.AddMinutes(15);
        if (newSession) session.RefreshExpiresAt = now.AddDays(30);
        return new(access, refresh, session.AccessExpiresAt, session.RefreshExpiresAt, User(session.Account));
    }
}
