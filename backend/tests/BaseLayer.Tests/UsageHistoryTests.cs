using BaseLayer.Application.Services;
using BaseLayer.Data;
using BaseLayer.Data.Repositories;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace BaseLayer.Tests;

public sealed class UsageHistoryTests : IDisposable
{
    private readonly PlatformDbContext db;
    private readonly TestClock clock = new();
    private readonly Home home;
    private readonly Device device;
    private readonly DatabaseGate gate = new();
    private readonly HomeOperationGate operations = new();
    private readonly UsageRecordingSession session = new();
    private UsageHistoryService Service(UsageRecordingSession? run = null) => new(new UsageHistoryRepository(db, gate), operations, clock, run ?? session);
    private UsageHistoryMaintenance Maintenance() => new(new UsageHistoryRepository(db, gate), clock);

    public UsageHistoryTests()
    {
        db = new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite("Data Source=:memory:").Options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        home = new() { OwnerId = "alice", Name = "Home", ProtectedTokens = "preserved-test-token" };
        device = new() { HomeId = home.Id, EntityId = "switch.heater", Name = "Heater", Present = true,
            State = "on", PowerSensorId = "sensor.heater_power", ShutoffLevel = ShutoffLevels.Never, Allowed = false };
        home.Devices.Add(device);
        db.Add(home);
        db.SaveChanges();
    }

    private async Task Observe(double? watts, int advanceSeconds = 0, UsageRecordingSession? run = null)
    {
        clock.Now = clock.Now.AddSeconds(advanceSeconds);
        home.LastSeenUtc = clock.Now;
        device.PowerWatts = watts;
        await db.SaveChangesAsync();
        await Service(run).RecordAsync(home.Id);
    }
    private Task<List<DeviceUsageBucket>> Minutes() => db.Set<DeviceUsageBucket>().AsNoTracking()
        .Where(b => b.Resolution == UsageResolutions.Minute).OrderBy(b => b.BucketStartUtc).ToListAsync();

    [Fact]
    public async Task ConstantLoadProduces25WhPerMinuteAndSnapshotRetriesDoNotDoubleCount()
    {
        await Observe(1500);
        for (var i = 0; i < 4; i++)
        {
            await Observe(1500, 15);
            await Service().RecordAsync(home.Id);
        }
        var first = (await Minutes())[0];
        Assert.Equal(60, first.CoveredSeconds);
        Assert.Equal(25, first.WattSeconds / 3600);
        Assert.Equal(4, first.SampleCount);
        Assert.Equal(1500, first.MinWatts);
        Assert.Single(await db.Set<DeviceStateEvent>().ToListAsync());
        var response = await Service().GetAsync("alice", home.Id, device.EntityId, clock.Now.AddMinutes(-1), clock.Now, "minute");
        var bucket = Assert.Single(response.Buckets);
        Assert.Equal(1500, bucket.AverageWatts);
        Assert.Equal(100, bucket.CoveragePercent);
        Assert.Equal(DateTimeKind.Utc, bucket.BucketStartUtc.Kind);
        Assert.False(device.Allowed); // Recording is independent of shutoff permissions.
    }

    [Fact]
    public async Task IntegratesActualElapsedTimeAndSplitsAtMinuteAndHourBoundaries()
    {
        clock.Now = clock.Now.AddMinutes(59).AddSeconds(55);
        await Observe(1200);
        await Observe(600, 10);
        await Observe(0, 7);
        var buckets = await Minutes();
        Assert.Equal(2, buckets.Count);
        Assert.Equal(5, buckets[0].CoveredSeconds);
        Assert.Equal(6000, buckets[0].WattSeconds);
        Assert.Equal(12, buckets[1].CoveredSeconds);
        Assert.Equal(10200, buckets[1].WattSeconds);
        Assert.Equal(0, buckets[1].MinWatts);
        Assert.Equal(1200, buckets[1].MaxWatts);
    }

    [Fact]
    public async Task InvalidReadingsOutagesAndDisappearingDevicesDoNotInventEnergy()
    {
        await Observe(1500);
        await Observe(null, 5);
        await Observe(1500, 5);
        await Observe(1500, 5); // Only these five seconds are covered.
        device.Present = false;
        await Observe(1500, 5);
        device.Present = true;
        await Observe(1500, 5);
        await Observe(1500, 21); // Too long to bridge.
        await Observe(null, 1); // The provider normalizes non-finite readings to unknown.
        await Observe(-1, 1);
        Assert.Equal(5, (await Minutes()).Sum(b => b.CoveredSeconds));
        var events = await db.Set<DeviceStateEvent>().OrderBy(e => e.ObservedAtUtc).ToListAsync();
        Assert.Contains(events, e => e.State == "unavailable");
        Assert.Equal(2, events.Count(e => e.StartsObservationSegment));
    }

    [Fact]
    public async Task UnchangedAndZeroPowerRemainValidAndOffDevicesCanConsumeStandby()
    {
        device.State = "off";
        await Observe(6);
        await Observe(6, 10);
        await Observe(0, 10);
        await Observe(0, 10);
        var bucket = Assert.Single(await Minutes());
        Assert.Equal(30, bucket.CoveredSeconds);
        Assert.Equal(120, bucket.WattSeconds);
        Assert.Equal(4, bucket.SampleCount);
    }

    [Fact]
    public async Task RestartAndFailedPollBeginNewSegmentsEvenInsideMaximumGap()
    {
        await Observe(1500);
        var restarted = new UsageRecordingSession();
        await Observe(1500, 5, restarted);
        await Observe(1500, 5, restarted);
        await Service(restarted).BreakObservationAsync(home.Id);
        await Observe(1500, 5, restarted);
        Assert.Equal(5, (await Minutes()).Sum(b => b.CoveredSeconds));
        Assert.Equal(3, await db.Set<DeviceStateEvent>().CountAsync(e => e.StartsObservationSegment));
    }

    [Fact]
    public async Task MappingChangesAndUnmappingCloseSeriesWithoutBridgingTheirEnergy()
    {
        await Observe(1000);
        await Observe(1000, 5);
        device.PowerSensorId = "sensor.new";
        device.PowerSensorRevision++;
        await Observe(2000, 5);
        await Observe(2000, 5);
        // Even A -> B -> A between observations must start a new series.
        device.PowerSensorRevision += 2;
        await Observe(3000, 5);
        device.PowerSensorId = null;
        device.PowerSensorRevision++;
        await Observe(null, 5);
        var series = await db.Set<DeviceMeasurementSeries>().ToListAsync();
        Assert.Equal(3, series.Count);
        Assert.All(series, s => Assert.NotNull(s.EndedUtc));
        Assert.Equal(10, (await Minutes()).Sum(b => b.CoveredSeconds));
        Assert.Equal(15000, (await Minutes()).Sum(b => b.WattSeconds));
    }

    [Fact]
    public async Task UnmeteredDevicesKeepStateHistoryWithoutUsageAndRenamesKeepTheirSeries()
    {
        device.PowerSensorId = null;
        await Observe(null);
        device.State = "off";
        await Observe(null, 5);
        Assert.Empty(await Minutes());
        Assert.Empty(await db.Set<DeviceMeasurementSeries>().ToListAsync());
        Assert.Equal(2, (await Service().GetStateEventsAsync("alice", home.Id, device.EntityId,
            clock.Now.AddMinutes(-1), clock.Now.AddMinutes(1))).Count);
        device.PowerSensorId = "sensor.power";
        await Observe(5, 5);
        device.Name = "Renamed";
        await Observe(5, 5);
        Assert.Single(await db.Set<DeviceMeasurementSeries>().ToListAsync());
    }

    [Fact]
    public async Task NoCoverageIsUnknownButMeasuredZeroIsZero()
    {
        await Observe(0);
        var first = await Service().GetAsync("alice", home.Id, device.EntityId, clock.Now, clock.Now.AddMinutes(1), "minute");
        Assert.Null(Assert.Single(first.Buckets).EstimatedEnergyWh);
        await Observe(0, 10);
        var next = await Service().GetAsync("alice", home.Id, device.EntityId, UsageHistoryService.FloorMinute(clock.Now), UsageHistoryService.FloorMinute(clock.Now).AddMinutes(1), "minute");
        Assert.Equal(0, Assert.Single(next.Buckets).EstimatedEnergyWh);
    }

    [Fact]
    public async Task StaleSnapshotsAndRevokedHomesAreNotRecorded()
    {
        await Observe(1000);
        clock.Now = clock.Now.AddSeconds(30);
        await Service().RecordAsync(home.Id);
        home.Revoked = true;
        await Observe(1000, 1);
        Assert.Equal(0, (await Minutes()).Sum(b => b.CoveredSeconds));
        Assert.Equal(1, (await Minutes()).Sum(b => b.SampleCount));
    }

    private async Task<DeviceMeasurementSeries> SeedMinutes(DateTime hour, params (int Minute, double Watts, double Seconds)[] values)
    {
        var series = new DeviceMeasurementSeries { HomeId = home.Id, DeviceId = device.Id, PowerSensorEntityId = "sensor.power", StartedUtc = hour };
        db.Add(series);
        foreach (var value in values)
            db.Add(new DeviceUsageBucket { MeasurementSeriesId = series.Id, BucketStartUtc = hour.AddMinutes(value.Minute),
                WattSeconds = value.Watts * value.Seconds, CoveredSeconds = value.Seconds,
                MinWatts = value.Watts, MaxWatts = value.Watts, SampleCount = 2, RollupPending = true });
        await db.SaveChangesAsync();
        return series;
    }

    [Fact]
    public async Task HourlyRollupWeightsByCoveragePreservesExtremesAndIsIdempotent()
    {
        var hour = clock.Now.AddHours(-2);
        await SeedMinutes(hour, (0, 100, 60), (1, 1000, 15));
        await Maintenance().RunAsync();
        await Maintenance().RunAsync();
        var bucket = await db.Set<DeviceUsageBucket>().SingleAsync(b => b.Resolution == "hour");
        Assert.Equal(75, bucket.CoveredSeconds);
        Assert.Equal(280, bucket.WattSeconds / bucket.CoveredSeconds);
        Assert.Equal(100, bucket.MinWatts);
        Assert.Equal(1000, bucket.MaxWatts);
        Assert.Equal(4, bucket.SampleCount);
        Assert.All(await Minutes(), b => Assert.False(b.RollupPending));
        var minute = await db.Set<DeviceUsageBucket>().SingleAsync(b => b.Resolution == "minute" && b.BucketStartUtc == hour.AddMinutes(1));
        minute.WattSeconds += 5000;
        minute.CoveredSeconds += 5;
        minute.RollupPending = true;
        await db.SaveChangesAsync();
        await Maintenance().RunAsync();
        Assert.Equal(26000, bucket.WattSeconds);
        Assert.Equal(80, bucket.CoveredSeconds);
    }

    [Fact]
    public async Task RollupWaitsForHourBoundaryAndLastObservationGracePeriod()
    {
        await SeedMinutes(clock.Now.AddHours(-1), (59, 100, 60));
        await Maintenance().RunAsync();
        Assert.False(await db.Set<DeviceUsageBucket>().AnyAsync(b => b.Resolution == "hour"));
        clock.Now = clock.Now.AddSeconds(21);
        await Maintenance().RunAsync();
        Assert.True(await db.Set<DeviceUsageBucket>().AnyAsync(b => b.Resolution == "hour"));
    }

    [Fact]
    public async Task RetentionCompactsOldMinutesBeforeDeletingAndKeepsHourlyDataForTwoYears()
    {
        var hour = clock.Now.AddDays(-91);
        await SeedMinutes(hour, (0, 1500, 60), (1, 1500, 30));
        await Maintenance().RunAsync();
        Assert.Empty(await Minutes());
        var hourly = await db.Set<DeviceUsageBucket>().SingleAsync();
        Assert.Equal(37.5, hourly.WattSeconds / 3600);
        Assert.Equal(90, hourly.CoveredSeconds);
        await Maintenance().RunAsync();
        Assert.Equal(135000, hourly.WattSeconds);
        clock.Now = clock.Now.AddYears(2);
        await Maintenance().RunAsync();
        Assert.Empty(await db.Set<DeviceUsageBucket>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FailedRollupRollsBackAndPreservesSourceMinutesForRetry()
    {
        await SeedMinutes(clock.Now.AddDays(-91), (0, 1500, 60));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_rollup BEFORE INSERT ON DeviceUsageBuckets WHEN NEW.Resolution = 'hour'
            BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Maintenance().RunAsync());
        db.ChangeTracker.Clear();
        Assert.True(Assert.Single(await Minutes()).RollupPending);
        Assert.False(await db.Set<DeviceUsageBucket>().AnyAsync(b => b.Resolution == "hour"));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_rollup");
        await Maintenance().RunAsync();
        Assert.Empty(await Minutes());
        Assert.Equal(90000, (await db.Set<DeviceUsageBucket>().SingleAsync()).WattSeconds);
    }

    [Fact]
    public async Task FailedCheckpointRollsBackBucketsAndRetryCountsTheIntervalOnce()
    {
        await Observe(1000);
        clock.Now = clock.Now.AddSeconds(10);
        home.LastSeenUtc = clock.Now;
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_checkpoint BEFORE UPDATE ON DeviceUsageCursors
            BEGIN SELECT RAISE(ABORT, 'simulated checkpoint failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Service().RecordAsync(home.Id));
        db.ChangeTracker.Clear();
        var beforeRetry = Assert.Single(await Minutes());
        Assert.Equal(0, beforeRetry.CoveredSeconds);
        Assert.Equal(1, beforeRetry.SampleCount);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_checkpoint");
        await Service().RecordAsync(home.Id);
        await Service().RecordAsync(home.Id);
        var afterRetry = Assert.Single(await Minutes());
        Assert.Equal(10, afterRetry.CoveredSeconds);
        Assert.Equal(10000, afterRetry.WattSeconds);
        Assert.Equal(2, afterRetry.SampleCount);
    }

    [Fact]
    public async Task QueriesEnforceOwnershipAndBoundedAlignedRanges()
    {
        await Observe(1000);
        var from = clock.Now;
        var to = from.AddMinutes(1);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service().GetAsync("bob", home.Id, device.EntityId, from, to, "minute"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service().GetStateEventsAsync("bob", home.Id, device.EntityId, from, to));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service().GetAsync("alice", Guid.NewGuid(), device.EntityId, from, to, "minute"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().GetAsync("alice", home.Id, device.EntityId, from, to, "day"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().GetAsync("alice", home.Id, device.EntityId, from, from.AddDays(8), "minute"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().GetAsync("alice", home.Id, device.EntityId, from, from.AddDays(94), "hour"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().GetAsync("alice", home.Id, device.EntityId, to, from, "minute"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().GetAsync("alice", home.Id, device.EntityId, from.AddSeconds(1), to, "minute"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service().GetStateEventsAsync("alice", home.Id, device.EntityId, from, from.AddDays(8)));
    }

    [Fact]
    public async Task DeletingHomeCascadesThroughAllHistory()
    {
        await Observe(1500);
        await Observe(1500, 10);
        await db.Homes.Where(h => h.Id == home.Id).ExecuteDeleteAsync();
        Assert.Empty(await db.Set<DeviceMeasurementSeries>().AsNoTracking().ToListAsync());
        Assert.Empty(await db.Set<DeviceUsageBucket>().AsNoTracking().ToListAsync());
        Assert.Empty(await db.Set<DeviceUsageCursor>().AsNoTracking().ToListAsync());
        Assert.Empty(await db.Set<DeviceStateEvent>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ExistingDatabaseUpgradeIsRepeatableAndPreservesHomeDeviceAndCredentials()
    {
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE DeviceUsageBuckets;
            DROP TABLE DeviceUsageCursors;
            DROP TABLE DeviceStateEvents;
            DROP TABLE DeviceMeasurementSeries;
            ALTER TABLE Device DROP COLUMN PowerSensorRevision;
            """);
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        var preserved = await db.Homes.AsNoTracking().Include(h => h.Devices).SingleAsync();
        Assert.Equal("preserved-test-token", preserved.ProtectedTokens);
        Assert.Equal(device.Id, Assert.Single(preserved.Devices).Id);
        Assert.Equal("sensor.heater_power", preserved.Devices[0].PowerSensorId);
        await Observe(1500);
        await Observe(1500, 10);
        Assert.Equal(15000, Assert.Single(await Minutes()).WattSeconds);
    }

    public void Dispose() => db.Dispose();
    private sealed class TestClock : TimeProvider
    {
        public DateTime Now { get; set; } = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
