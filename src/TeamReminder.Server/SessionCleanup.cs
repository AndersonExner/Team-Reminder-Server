using Microsoft.EntityFrameworkCore;

namespace TeamReminder.Server;

// Expired sessions can never authenticate again; remove them so the table does not grow without bound.
public sealed class SessionCleanup(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SessionCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AuthDb>();
                var now = clock.GetUtcNow();
                var removed = await db.Sessions.Where(x => x.RefreshExpiresAt <= now).ExecuteDeleteAsync(stoppingToken);
                if (removed > 0) logger.LogInformation("Removed {Count} expired sessions.", removed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Expired session cleanup failed; it will run again later.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
