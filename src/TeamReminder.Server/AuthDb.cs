using Microsoft.EntityFrameworkCore;

namespace TeamReminder.Server;

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public long AlarmRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LoginSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public string AccessHash { get; set; } = "";
    public string RefreshHash { get; set; } = "";
    public DateTimeOffset AccessExpiresAt { get; set; }
    public DateTimeOffset RefreshExpiresAt { get; set; }
}

public sealed class AuthDb(DbContextOptions<AuthDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LoginSession> Sessions => Set<LoginSession>();
    public DbSet<PersonalAlarm> PersonalAlarms => Set<PersonalAlarm>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<PersonalAlarm>(e =>
        {
            e.HasKey(x => new { x.AccountId, x.Id });
            e.HasIndex(x => new { x.AccountId, x.Version }).IsUnique();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.NormalizedEmail).HasMaxLength(254);
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
        });
        model.Entity<LoginSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AccessHash).IsUnique();
            e.HasIndex(x => x.RefreshHash).IsUnique();
            e.Property(x => x.RefreshHash).IsConcurrencyToken();
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
