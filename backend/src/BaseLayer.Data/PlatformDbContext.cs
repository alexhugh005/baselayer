using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
namespace BaseLayer.Data;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Home> Homes => Set<Home>();
    public DbSet<RestoreQueueEntry> RestoreQueueEntries => Set<RestoreQueueEntry>();
    public DbSet<OAuthState> OAuthStates => Set<OAuthState>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        UsageModelConfiguration.Configure(b);
        b.Entity<Home>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Home>().Property(x => x.PowerSource).HasDefaultValue(HouseholdPowerSources.WholeHouseMeter);
        b.Entity<Device>().Property(x => x.Id).ValueGeneratedNever();
        var restore = b.Entity<RestoreQueueEntry>();
        restore.ToTable("RestoreQueueEntries");
        restore.HasKey(e => e.DeviceId);
        restore.Property(e => e.DeviceId).ValueGeneratedNever();
        restore.HasOne(e => e.Device).WithOne(d => d.RestoreEntry)
            .HasForeignKey<RestoreQueueEntry>(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
        restore.HasIndex(e => new { e.AtFront, e.QueuedUtc });
        b.Entity<DeviceCommand>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<OAuthState>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Home>().HasMany(h => h.Devices).WithOne().HasForeignKey(d => d.HomeId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Home>().HasMany(h => h.Commands).WithOne().HasForeignKey(d => d.HomeId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Device>().HasIndex(d => new { d.HomeId, d.EntityId }).IsUnique();
        b.Entity<DeviceCommand>().HasIndex(d => new { d.HomeId, d.IdempotencyKey, d.EntityId }).IsUnique();
        b.Entity<OAuthState>().HasIndex(p => p.CodeHash).IsUnique();
    }
}
