using BaseLayer.Application.Contracts;
using BaseLayer.Data;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    private EnqueueAnomalyRequest Anomaly(string eventId = "event-1", double usual = 1000, double observed = 5000, string action = "Off") =>
        new(eventId, "switch.dryer", usual, observed, action, clock.GetUtcNow().UtcDateTime);

    [Theory]
    [InlineData(false, "Sometimes", 1000, 5000, "Off", false)]
    [InlineData(true, "Never", 1000, 5000, "Off", false)]
    [InlineData(true, "Sometimes", 1000, 5000, "Off", true)]
    [InlineData(true, "Anytime", 1000, 5000, "Off", true)]
    [InlineData(true, "Sometimes", 6000, 5000, "Off", false)]
    [InlineData(true, "Anytime", 1000, 5000, "Inspect", false)]
    public async Task AnomalyPolicyHonorsSettingPermissionsAndRecommendation(bool enabled, string level, double usual, double observed, string action, bool sends)
    {
        var home = await Connect();
        await Allow(home);
        var entity = await db.Homes.Include(h => h.Devices).SingleAsync();
        entity.Devices[0].ShutoffLevel = level;
        await db.SaveChangesAsync();
        gridRisk.Risk = GridOutageRisk.Low;
        await service.AnomalySavingsAsync("alice", home.Id, new(enabled));
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly(usual: usual, observed: observed, action: action));
        db.ChangeTracker.Clear(); // The queue and setting survive a new unit of work.
        await service.PollAsync(home.Id);
        var result = (await service.HomesAsync("alice"))[0];
        Assert.Equal(sends ? 1 : 0, provider.Sends);
        Assert.Equal(sends ? "AwaitingConfirmation" : "NotificationOnly", Assert.Single(result.Anomalies!).Status);
        Assert.Equal(0, result.AnomalySavings!.EstimatedSavedKwh);
        Assert.Empty(result.RestoreQueue!);
    }

    [Fact]
    public async Task AnomalySavingsCreditsOnlyConfirmedObservedOffIntervalsAndCapsAtOneHour()
    {
        var home = await Connect();
        await Allow(home);
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly());
        await service.PollAsync(home.Id);
        provider.Off = true;
        await service.PollAsync(home.Id);
        for (var i = 0; i < 180; i++) { clock.Advance(20); await service.PollAsync(home.Id); }
        clock.Advance(20);
        await service.PollAsync(home.Id);
        var result = (await service.HomesAsync("alice"))[0];
        Assert.Equal(4, result.AnomalySavings!.EstimatedSavedKwh, 8);
        Assert.Equal(1, result.AnomalySavings.ConfirmedActions);
        Assert.Equal("Confirmed", Assert.Single(result.Anomalies!).Status);
        Assert.NotNull(Assert.Single(result.Commands).AnomalyId);
        Assert.Equal(0, provider.TurnOns);
    }

    [Fact]
    public async Task AnomalySavingsExcludesGapsAndClosesWhenDeviceRestarts()
    {
        var home = await Connect();
        await Allow(home);
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly());
        await service.PollAsync(home.Id);
        provider.Off = true;
        await service.PollAsync(home.Id);
        clock.Advance(600);
        await service.PollAsync(home.Id);
        Assert.Equal(0, (await service.HomesAsync("alice"))[0].AnomalySavings!.EstimatedSavedKwh);
        clock.Advance(18);
        await service.PollAsync(home.Id);
        provider.Off = false;
        clock.Advance(18);
        await service.PollAsync(home.Id);
        provider.Off = true;
        clock.Advance(18);
        await service.PollAsync(home.Id);
        Assert.Equal(0.02, (await service.HomesAsync("alice"))[0].AnomalySavings!.EstimatedSavedKwh, 8);
    }

    [Fact]
    public async Task AnomalyQueueIsTenantScopedIdempotentAndRejectsConflictingPayloads()
    {
        var home = await Connect();
        var request = Anomaly();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.EnqueueAnomalyAsync("bob", home.Id, request));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.AnomalySavingsAsync("bob", home.Id, new(true)));
        var first = await service.EnqueueAnomalyAsync("alice", home.Id, request);
        Assert.Equal(first.Id, (await service.EnqueueAnomalyAsync("alice", home.Id, request)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.EnqueueAnomalyAsync("alice", home.Id, request with { ObservedWatts = 6000 }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EnqueueAnomalyAsync("alice", home.Id, request with { UsualWatts = double.NaN }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EnqueueAnomalyAsync("alice", home.Id, request with { RecommendedAction = "On" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EnqueueAnomalyAsync("alice", home.Id, request with { EntityId = "switch.unknown" }));
        Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!);
    }

    [Fact]
    public async Task AnomalyRetriesAreBoundedAndDisablingSettingStopsThem()
    {
        var home = await Connect();
        await Allow(home);
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly());
        provider.Fail = true;
        await service.PollAsync(home.Id);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        await service.SmartPowerOffAsync("alice", home.Id, new(false)); // Independent setting.
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.Sends);
        await service.AnomalySavingsAsync("alice", home.Id, new(false));
        clock.Advance(10);
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.Sends);
        var result = (await service.HomesAsync("alice"))[0];
        Assert.Equal("Cancelled", Assert.Single(result.Anomalies!).Status);
        Assert.Equal(0, result.AnomalySavings!.EstimatedSavedKwh);
    }

    [Fact]
    public async Task AnomalyStaleEventsNormalizedReadingsAndRepeatedDeviceEventsDoNotAct()
    {
        var home = await Connect();
        await Allow(home);
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly("stale") with { DetectedUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(-6) });
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly("normalized", usual: 5500, observed: 6000));
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.Sends);
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly());
        await service.PollAsync(home.Id);
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly("same-device"));
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        Assert.Equal(3, (await service.HomesAsync("alice"))[0].Anomalies!.Count(a => a.Status == "NotificationOnly"));
    }

    [Fact]
    public async Task AnomalyPermissionRemovalCancelsRetry()
    {
        var home = await Connect();
        await Allow(home);
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly());
        await service.PollAsync(home.Id);
        await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total", new() { ["switch.dryer"] = "sensor.dryer" }));
        clock.Advance(15);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        Assert.Equal("Cancelled", Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!).Status);
    }

    [Fact]
    public async Task AnomalyUpgradeIsRepeatableAndPreservesExistingHomes()
    {
        var home = await Connect();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE UsageAnomalies");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN AnomalySavingsEnabled");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN AnomalyId");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        db.ChangeTracker.Clear();
        Assert.False((await service.HomesAsync("alice"))[0].AnomalySavingsEnabled);
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly());
        db.ChangeTracker.Clear();
        Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!);
    }
    [Fact]
    public async Task FailedAnomalyCommandsDoNotEarnSavingsOrRetryForever()
    {
        var home = await Connect();
        await Allow(home);
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly());
        provider.Fail = true;
        await service.PollAsync(home.Id);
        foreach (var delay in new[] { 5, 10, 20, 40 })
        {
            clock.Advance(delay);
            await service.PollAsync(home.Id);
        }
        provider.Off = true;
        await service.PollAsync(home.Id);
        var result = (await service.HomesAsync("alice"))[0];
        Assert.Equal(4, provider.Sends);
        Assert.Equal("Failed", Assert.Single(result.Anomalies!).Status);
        Assert.Equal(0, result.AnomalySavings!.EstimatedSavedKwh);
        Assert.Equal(0, result.AnomalySavings.ConfirmedActions);
    }

    [Theory]
    [InlineData("climate.room")]
    [InlineData("switch.span_panel_kitchen_breaker")]
    public async Task AnomalyRespectsExistingThermostatAndCircuitRestrictions(string entityId)
    {
        provider.Snapshot = new([new(entityId, "Protected device", "on")], [new("sensor.device", "Power", "W")],
            new() { ["sensor.device"] = 5000, ["sensor.span_panel_kitchen_power"] = 5000 });
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, new(true, false, [], null, new() { [entityId] = "sensor.device" }));
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        await service.EnqueueAnomalyAsync("alice", home.Id, Anomaly() with { EntityId = entityId });
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.Sends);
        Assert.Equal("NotificationOnly", Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!).Status);
    }

}
