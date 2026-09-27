using BaseLayer.Application.Contracts;
using BaseLayer.Data;
using BaseLayer.Application.Services;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    private static HomeSettingsRequest StandardSettings(string? category = "toaster", double? watts = null) =>
        new(true, false, [], "sensor.total", new() { ["switch.dryer"] = "sensor.dryer" },
            DevicePowerStandards: new() { ["switch.dryer"] = category is null ? null : new(category, watts) });

    private void StandardReading(double? watts, string state = "on") => provider.Snapshot = new(
        [new("switch.dryer", "Test appliance", state)], [new("sensor.dryer", "Power", "W"), new("sensor.total", "Total", "W")],
        new() { ["sensor.dryer"] = watts, ["sensor.total"] = watts });

    [Theory]
    [InlineData(1799.99, false)]
    [InlineData(1800.0, true)]
    [InlineData(1801.0, true)]
    [InlineData(1200.0, false)]
    [InlineData(0.0, false)]
    [InlineData(null, false)]
    public async Task StandardDetectionUsesFreshReadingsAndInclusiveFiftyPercentBoundary(double? watts, bool triggers)
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        gridRisk.Risk = GridOutageRisk.Low;
        StandardReading(watts);
        await service.PollAsync(home.Id);
        var result = (await service.HomesAsync("alice"))[0];
        Assert.Equal(triggers ? 1 : 0, result.Anomalies!.Count);
        Assert.Equal(triggers ? 1 : 0, provider.Sends);
        if (triggers)
        {
            var anomaly = Assert.Single(result.Anomalies!);
            Assert.Equal(1200, anomaly.UsualWatts);
            Assert.Equal(watts, anomaly.ObservedWatts);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void StandardPolicyRejectsNonfinitePower(double watts) => Assert.False(DevicePowerStandards.Exceeds(watts, 1200));

    [Theory]
    [InlineData(false, "Sometimes")]
    [InlineData(true, "Never")]
    public async Task StandardDetectionNotifiesWithoutPermissionAndDeduplicatesAcrossReload(bool enabled, string level)
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings() with { ShutoffLevels = new() { ["switch.dryer"] = level } });
        await service.AnomalySavingsAsync("alice", home.Id, new(enabled));
        StandardReading(1800);
        await service.PollAsync(home.Id);
        db.ChangeTracker.Clear();
        clock.Advance(3);
        await service.PollAsync(home.Id);
        Assert.Equal("NotificationOnly", Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!).Status);
        Assert.Equal(0, provider.Sends);
        clock.Advance(3600);
        await service.PollAsync(home.Id);
        Assert.Equal(2, (await service.HomesAsync("alice"))[0].Anomalies!.Count);
    }

    [Fact]
    public async Task CategoryDefaultsOverridesClearingAndOmittedSettingsPersist()
    {
        var home = await Connect();
        var configured = await service.SettingsAsync("alice", home.Id, StandardSettings());
        Assert.Equal(1200, Assert.Single(configured.Devices).StandardWatts);
        Assert.Equal(1800, Assert.Single(configured.Devices).AnomalyThresholdWatts);
        Assert.Contains(configured.DeviceCategories!, c => c.Id == "toaster" && c.StandardWatts == 1200);
        await service.SettingsAsync("alice", home.Id, StandardSettings(watts: 1400));
        db.ChangeTracker.Clear();
        configured = await Allow(home); // Older clients preserve category settings.
        Assert.Equal(1400, Assert.Single(configured.Devices).StandardWatts);
        Assert.Equal(2100, Assert.Single(configured.Devices).AnomalyThresholdWatts);
        configured = await service.SettingsAsync("alice", home.Id, StandardSettings(null));
        Assert.Null(Assert.Single(configured.Devices).StandardWatts);
        StandardReading(9999);
        await service.PollAsync(home.Id);
        Assert.Empty((await service.HomesAsync("alice"))[0].Anomalies!);
    }

    [Theory]
    [InlineData("bogus", null)]
    [InlineData("custom", null)]
    [InlineData("toaster", 0.0)]
    [InlineData("toaster", -1.0)]
    [InlineData("toaster", double.NaN)]
    [InlineData("toaster", 1000001.0)]
    public async Task InvalidCategoryStandardsAreRejected(string category, double? watts)
    {
        var home = await Connect();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SettingsAsync("alice", home.Id, StandardSettings(category, watts)));
    }

    [Fact]
    public async Task ChangingStandardCancelsPendingActionAndNewStandardIsUsed()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        StandardReading(1800);
        provider.Fail = true;
        await service.PollAsync(home.Id);
        await service.SettingsAsync("alice", home.Id, StandardSettings("custom", 2000));
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        Assert.Equal("Cancelled", Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!).Status);
        clock.Advance(3600);
        await service.PollAsync(home.Id);
        Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!);
    }

    [Fact]
    public async Task RetryStopsWhenPowerFallsBelowFiftyPercentEvenIfStillAboveStandard()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        provider.Fail = true;
        StandardReading(1800);
        await service.PollAsync(home.Id);
        StandardReading(1700);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        Assert.Equal("Cancelled", Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!).Status);
    }

    [Fact]
    public async Task StandardSchemaUpgradeIsRepeatableAndKeepsDevicesUnassigned()
    {
        var home = await Connect();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN StandardPowerAnomalyActive");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN Category");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN StandardWattsOverride");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        db.ChangeTracker.Clear();
        Assert.Null(Assert.Single((await service.HomesAsync("alice"))[0].Devices).Category);
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        db.ChangeTracker.Clear();
        Assert.Equal(1200, Assert.Single((await service.HomesAsync("alice"))[0].Devices).StandardWatts);
    }
    [Fact]
    public async Task RestartedDeviceNotifiesAgainDuringShutoffCooldownWithoutSendingAgain()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        await service.AnomalySavingsAsync("alice", home.Id, new(true));
        StandardReading(1800);
        await service.PollAsync(home.Id);
        StandardReading(0, "off");
        clock.Advance(3);
        await service.PollAsync(home.Id);
        Assert.Equal("Confirmed", Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!).Status);
        db.ChangeTracker.Clear();
        StandardReading(1800);
        clock.Advance(3);
        await service.PollAsync(home.Id);
        var events = (await service.HomesAsync("alice"))[0].Anomalies!;
        Assert.Equal(2, events.Count);
        Assert.Equal("NotificationOnly", events[0].Status);
        Assert.Contains("within the last hour", events[0].Message);
        Assert.Equal(1, provider.Sends);
        db.ChangeTracker.Clear();
        clock.Advance(3);
        await service.PollAsync(home.Id);
        Assert.Equal(2, (await service.HomesAsync("alice"))[0].Anomalies!.Count);
    }

    [Fact]
    public async Task NormalReadingsRearmDetectionButMissingTelemetryDoesNot()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        StandardReading(1800);
        await service.PollAsync(home.Id);
        StandardReading(null, "unavailable");
        clock.Advance(3);
        await service.PollAsync(home.Id);
        db.ChangeTracker.Clear();
        StandardReading(1800);
        clock.Advance(3);
        await service.PollAsync(home.Id);
        Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!);
        StandardReading(1200);
        clock.Advance(3);
        await service.PollAsync(home.Id);
        StandardReading(1800);
        clock.Advance(3);
        await service.PollAsync(home.Id);
        Assert.Equal(2, (await service.HomesAsync("alice"))[0].Anomalies!.Count);
    }

    [Fact]
    public async Task PersistedDetectionTimesSerializeWithUtcMarkerForBrowserAlerts()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        StandardReading(1800);
        await service.PollAsync(home.Id);
        db.ChangeTracker.Clear();
        var anomaly = Assert.Single((await service.HomesAsync("alice"))[0].Anomalies!);
        Assert.Equal(DateTimeKind.Utc, anomaly.DetectedUtc.Kind);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, anomaly.DetectedUtc);
        var json = System.Text.Json.JsonSerializer.Serialize(anomaly, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.EndsWith("Z", document.RootElement.GetProperty("detectedUtc").GetString());
    }

    [Fact]
    public async Task ApprovedAnomalyUsesAnomalyWorkerAndNeverEntersRestorationQueue()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        await service.SmartPowerOffAsync("alice", home.Id, new(true));
        gridRisk.Risk = GridOutageRisk.Low;
        StandardReading(1800);
        await service.PollAsync(home.Id);
        var request = new TurnOffRequest(["switch.dryer"], "approved-anomaly", ForAnomaly: true);
        var command = Assert.Single(await service.TurnOffAsync("alice", home.Id, request));
        Assert.NotNull(command.AnomalyId);
        Assert.False(command.Automatic);
        Assert.Equal(command.Id, Assert.Single(await service.TurnOffAsync("alice", home.Id, request)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.TurnOffAsync("alice", home.Id, request with { ForAnomaly = false }));
        // An explicit approval does not depend on either automatic setting.
        await service.AnomalySavingsAsync("alice", home.Id, new(false));
        db.ChangeTracker.Clear();
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        Assert.Empty((await service.HomesAsync("alice"))[0].RestoreQueue!);
        StandardReading(0, "off");
        await service.PollAsync(home.Id);
        clock.Advance(30);
        await service.PollAsync(home.Id);
        clock.Advance(30);
        await service.PollAsync(home.Id);
        var result = (await service.HomesAsync("alice"))[0];
        Assert.Empty(result.RestoreQueue!);
        Assert.Equal("Confirmed", result.Commands.Single(c => c.Id == command.Id).Status);
        Assert.Contains("Anomaly detection", result.Commands.Single(c => c.Id == command.Id).Message);
        Assert.Equal(0, provider.TurnOns);
    }

    [Fact]
    public async Task ApprovedAnomalyStillRechecksPowerBeforeDispatch()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        StandardReading(1800);
        await service.PollAsync(home.Id);
        var command = Assert.Single(await service.TurnOffAsync("alice", home.Id, new(["switch.dryer"], "approved", true)));
        StandardReading(1700);
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.Sends);
        Assert.Equal("Cancelled", (await service.HomesAsync("alice"))[0].Commands.Single(c => c.Id == command.Id).Status);
    }

    [Fact]
    public async Task ApprovedAnomalyRejectsNormalPowerMissingStandardsAndOtherOwners()
    {
        var home = await Connect();
        await Allow(home);
        await service.PollAsync(home.Id);
        var request = new TurnOffRequest(["switch.dryer"], "approved", true);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.TurnOffAsync("bob", home.Id, request));
        await Assert.ThrowsAsync<ArgumentException>(() => service.TurnOffAsync("alice", home.Id, request));
        await service.SettingsAsync("alice", home.Id, StandardSettings());
        StandardReading(1700);
        await service.PollAsync(home.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.TurnOffAsync("alice", home.Id, request));
        Assert.Empty((await service.HomesAsync("alice"))[0].Commands);
    }

}
