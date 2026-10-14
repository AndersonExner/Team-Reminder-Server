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
    public bool IsActive { get; set; } = true;
    public bool IsSystemAdmin { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long Revision { get; set; } = 1;
    public long SessionVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LoginSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public long AccountVersion { get; set; }
    public string AccessHash { get; set; } = "";
    public string RefreshHash { get; set; } = "";
    public DateTimeOffset AccessExpiresAt { get; set; }
    public DateTimeOffset RefreshExpiresAt { get; set; }
}

public sealed class AuthDb(DbContextOptions<AuthDb> options) : DbContext(options)
{
    public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LoginSession> Sessions => Set<LoginSession>();
    public DbSet<PersonalAlarm> PersonalAlarms => Set<PersonalAlarm>();
    public DbSet<TeamGroup> Groups => Set<TeamGroup>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<GroupInvitation> GroupInvitations => Set<GroupInvitation>();
    public DbSet<GroupAuditEvent> GroupAuditEvents => Set<GroupAuditEvent>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AdminAuditEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.ActorAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<TeamGroup>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.OwnerAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupMembership>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.AccountId });
            e.HasIndex(x => x.AccountId);
            e.HasOne<TeamGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<GroupInvitation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.RecipientAccountId, x.Status });
            e.HasIndex(x => new { x.RecipientAccountId, x.Status });
            e.HasOne<TeamGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.RecipientAccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.InviterAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupAuditEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.CreatedAt });
            e.HasOne<TeamGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.ActorAccountId).OnDelete(DeleteBehavior.Restrict);
        });
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
