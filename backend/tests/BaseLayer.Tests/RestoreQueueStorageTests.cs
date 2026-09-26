using BaseLayer.Application.Services;
using BaseLayer.Data;
using BaseLayer.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    // Rebuild the pre-relational layout from persisted rows, including the pre-EV variant.
    private async Task RecreateLegacyRestoreColumns(bool includeEv = true)
    {
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE Device ADD COLUMN RestoreQueuedUtc TEXT NULL;
            ALTER TABLE Device ADD COLUMN RestoreWatts REAL NULL;
            ALTER TABLE Device ADD COLUMN RestoreEligibleSinceUtc TEXT NULL;
            ALTER TABLE Device ADD COLUMN RestoreStatus TEXT NOT NULL DEFAULT 'waiting';
            UPDATE Device SET
                RestoreQueuedUtc = (SELECT QueuedUtc FROM RestoreQueueEntries WHERE DeviceId = Device.Id),
                RestoreWatts = (SELECT EstimatedWatts FROM RestoreQueueEntries WHERE DeviceId = Device.Id),
                RestoreEligibleSinceUtc = (SELECT EligibleSinceUtc FROM RestoreQueueEntries WHERE DeviceId = Device.Id),
                RestoreStatus = COALESCE((SELECT Status FROM RestoreQueueEntries WHERE DeviceId = Device.Id), 'waiting');
            """);
        if (includeEv)
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE Device ADD COLUMN RestorePowerOn INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE Device ADD COLUMN RestoreCurrentAmps REAL NULL;
                ALTER TABLE Device ADD COLUMN LastManagedCurrentAmps REAL NULL;
                ALTER TABLE Device ADD COLUMN RestoreAtFront INTEGER NOT NULL DEFAULT 0;
                UPDATE Device SET
                    RestorePowerOn = COALESCE((SELECT PowerOn FROM RestoreQueueEntries WHERE DeviceId = Device.Id), 0),
                    RestoreCurrentAmps = (SELECT TargetCurrentAmps FROM RestoreQueueEntries WHERE DeviceId = Device.Id),
                    LastManagedCurrentAmps = (SELECT LastManagedCurrentAmps FROM RestoreQueueEntries WHERE DeviceId = Device.Id),
                    RestoreAtFront = COALESCE((SELECT AtFront FROM RestoreQueueEntries WHERE DeviceId = Device.Id), 0);
                """);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE RestoreQueueEntries");
    }

    private async Task<List<string>> DeviceColumnNames()
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA table_info('Device')";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    [Fact]
    public async Task RestoreMigrationPreservesQueueStateAndSharedDeviceMetadata()
    {
        var home = await QueueLoads();
        var entries = await db.RestoreQueueEntries.Include(e => e.Device).OrderBy(e => e.Device.EntityId).ToListAsync();
        entries[0].TargetCurrentAmps = 32;
        entries[0].LastManagedCurrentAmps = 20;
        entries[0].AtFront = true;
        entries[0].PowerOn = false;
        entries[0].Status = "restoring";
        entries[0].EligibleSinceUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(-4);
        entries[1].Status = "failed";
        entries[2].EstimatedWatts = null;
        await db.SaveChangesAsync();
        var expected = entries.Select(e => new { e.DeviceId, e.QueuedUtc, e.EstimatedWatts, e.TargetCurrentAmps,
            e.LastManagedCurrentAmps, e.AtFront, e.PowerOn, e.EligibleSinceUtc, e.Status }).ToArray();
        var devices = await db.Set<Device>().AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.HomeId, d.EntityId, d.Name, d.Allowed, d.ShutoffLevel, d.PowerSensorId }).ToArrayAsync();
        await RecreateLegacyRestoreColumns();
        // Leftover estimates without a queue timestamp must not become new entries.
        await db.Database.ExecuteSqlRawAsync("UPDATE Device SET RestoreWatts = 123 WHERE RestoreQueuedUtc IS NULL");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        var restored = await db.RestoreQueueEntries.Include(e => e.Device).OrderBy(e => e.Device.EntityId).ToListAsync();
        Assert.Equal(expected, restored.Select(e => new { e.DeviceId, e.QueuedUtc, e.EstimatedWatts, e.TargetCurrentAmps,
            e.LastManagedCurrentAmps, e.AtFront, e.PowerOn, e.EligibleSinceUtc, e.Status }).ToArray());
        Assert.Equal(devices, await db.Set<Device>().AsNoTracking().OrderBy(d => d.Id)
            .Select(d => new { d.Id, d.HomeId, d.EntityId, d.Name, d.Allowed, d.ShutoffLevel, d.PowerSensorId }).ToArrayAsync());
        Assert.DoesNotContain(await DeviceColumnNames(), c => c.StartsWith("Restore") || c == "LastManagedCurrentAmps");
        db.ChangeTracker.Clear();
        var dto = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal(home.Id, dto.Id);
        Assert.Equal(3, dto.RestoreQueue!.Count);
        Assert.Equal(32, dto.RestoreQueue[0].TargetAmps);
    }

    [Fact]
    public async Task KeepOffDeletesOnlyQueueRowAndDoesNotReappearAfterInitialization()
    {
        var home = await QueueLoads();
        var deviceId = await db.Set<Device>().Where(d => d.HomeId == home.Id && d.EntityId == "switch.large").Select(d => d.Id).SingleAsync();
        await service.KeepOffAsync("alice", home.Id, "switch.large");
        db.ChangeTracker.Clear();
        await DatabaseInitializer.InitializeAsync(db);
        Assert.False(await db.RestoreQueueEntries.AnyAsync(e => e.DeviceId == deviceId));
        Assert.True(await db.Set<Device>().AnyAsync(d => d.Id == deviceId));
        var saved = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal(2, saved.RestoreQueue!.Count);
        Assert.DoesNotContain(saved.RestoreQueue, e => e.EntityId == "switch.large");
        // A later shutoff can legitimately create a new entry for the same device.
        var device = await db.Set<Device>().SingleAsync(d => d.Id == deviceId);
        AutoRestorePolicy.Enqueue(device, clock.GetUtcNow().UtcDateTime).PowerOn = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.RestoreQueueEntries.CountAsync());
    }

    [Fact]
    public async Task RestoreQueueEnforcesDeviceForeignKeyAndOneEntryPerDevice()
    {
        await QueueLoads();
        var entry = await db.RestoreQueueEntries.AsNoTracking().FirstAsync();
        var missingId = Guid.NewGuid();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO RestoreQueueEntries (DeviceId, QueuedUtc, AtFront, PowerOn, Status)
            VALUES ({missingId}, {entry.QueuedUtc}, 0, 1, 'waiting')
            """));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO RestoreQueueEntries (DeviceId, QueuedUtc, AtFront, PowerOn, Status)
            VALUES ({entry.DeviceId}, {entry.QueuedUtc}, 0, 1, 'waiting')
            """));
        Assert.Equal(3, await db.RestoreQueueEntries.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreQueueCascadesWhenDeviceOrHomeIsDeleted(bool deleteHome)
    {
        var home = await QueueLoads();
        var deviceId = await db.RestoreQueueEntries.Select(e => e.DeviceId).FirstAsync();
        db.ChangeTracker.Clear();
        if (deleteHome) await db.Homes.Where(h => h.Id == home.Id).ExecuteDeleteAsync();
        else await db.Set<Device>().Where(d => d.Id == deviceId).ExecuteDeleteAsync();
        Assert.False(await db.RestoreQueueEntries.AnyAsync(e => e.DeviceId == deviceId));
        Assert.Equal(deleteHome ? 0 : 2, await db.RestoreQueueEntries.CountAsync());
    }

    [Fact]
    public async Task DevicesWithSameEntityIdInDifferentHomesHaveIndependentQueueEntries()
    {
        var first = await QueueLoads();
        var second = new Home { OwnerId = "bob", Name = "Other home" };
        var device = new Device { HomeId = second.Id, EntityId = "switch.large" };
        second.Devices.Add(device);
        AutoRestorePolicy.Enqueue(device, clock.GetUtcNow().UtcDateTime).EstimatedWatts = 42;
        db.Homes.Add(second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await service.KeepOffAsync("alice", first.Id, "switch.large");
        db.ChangeTracker.Clear();
        var other = Assert.Single(await service.HomesAsync("bob"));
        Assert.Equal(42, Assert.Single(other.RestoreQueue!).EstimatedWatts);
        Assert.Equal(3, await db.RestoreQueueEntries.CountAsync());
    }
}
