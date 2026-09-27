using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Data;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    private static string Breaker(string key) => $"switch.span_panel_{key}_breaker";
    private void RecoverySnapshot(string mode, params (string Key, string State, double? Watts, double? Estimate)[] rows)
    {
        var power = rows.ToDictionary(r => SmartPanelCircuit.PowerEntityId(Breaker(r.Key)), r => r.Watts);
        power["sensor.total"] = rows.All(r => r.Watts is not null) ? rows.Sum(r => r.Watts!.Value) : null;
        provider.Snapshot = new(rows.Select(r => new ProviderDevice(Breaker(r.Key), r.Key, r.State)).ToList(),
            power.Keys.Select(id => new PowerSensorDto(id, id, "W")).ToList(), power,
            PowerSupplies: [new("sensor.span_panel_grid_forming_entity", mode)],
            CircuitRestoreEstimates: rows.Where(r => r.Estimate is not null).ToDictionary(r => Breaker(r.Key), r => r.Estimate!.Value),
            BackupReady: true);
    }
    private async Task<HomeDto> RecoveryHome()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total", new()));
        await service.PollAsync(home.Id);
        return home;
    }
    private async Task<OutageRecoveryStatusDto> RecoveryStatus() => Assert.Single(await service.HomesAsync("alice")).OutageRecovery!;

    [Fact]
    public async Task OutageUsesNativeCircuitMetersWithoutAppliancePowerMappings()
    {
        RecoverySnapshot("grid", ("a", "on", 100, 100));
        var home = await Connect();
        Assert.Null(home.HouseholdWatts);
        RecoverySnapshot("battery", ("a", "off", 0, 100));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
    }

    [Fact]
    public async Task OutageRestoresLowestFirstOnlyPreviouslyOnAndWaitsFiveSecondsAfterConfirmation()
    {
        RecoverySnapshot("grid", ("large", "on", 1200, 1200), ("small", "on", 100, 100), ("off", "off", 0, 50));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("large", "off", 0, 1200), ("small", "off", 0, 100), ("off", "off", 0, 50));
        await service.PollAsync(home.Id);
        clock.Advance(4);
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.TurnOns);
        clock.Advance(1);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        var command = Assert.Single((await service.HomesAsync("alice"))[0].Commands);
        Assert.Equal(Breaker("small"), command.EntityId);
        Assert.NotNull(command.OutageEventId);
        // A successful service call alone does not permit a second restoration.
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        RecoverySnapshot("battery", ("large", "off", 0, 1200), ("small", "on", 100, 100), ("off", "off", 0, 50));
        await service.PollAsync(home.Id);
        clock.Advance(4);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        clock.Advance(1);
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.TurnOns);
        RecoverySnapshot("battery", ("large", "on", 1200, 1200), ("small", "on", 100, 100), ("off", "off", 0, 50));
        await service.PollAsync(home.Id);
        Assert.Equal("complete", (await RecoveryStatus()).Status);
        Assert.DoesNotContain((await RecoveryStatus()).Circuits, c => c.EntityId == Breaker("off"));
        Assert.False((await service.HomesAsync("alice"))[0].SmartPowerOffEnabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task OutageUsesSettledMeasuredLoadInsteadOfPermanentlyReservingIdlePeak(double idleWatts)
    {
        RecoverySnapshot("grid", ("small", "on", 10, 5000), ("large", "on", 10, 6001));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("small", "off", 0, 5000), ("large", "off", 0, 6001));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        RecoverySnapshot("battery", ("small", "on", idleWatts, 5000), ("large", "off", 0, 6001));
        await service.PollAsync(home.Id);
        clock.Advance(4);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        db.ChangeTracker.Clear(); // Timing and progress survive a reload.
        clock.Advance(1);
        await service.PollAsync(home.Id);
        var status = await RecoveryStatus();
        Assert.Equal(idleWatts, status.MeasuredWatts);
        Assert.Equal(idleWatts, status.ReservedWatts);
        Assert.Equal(2, provider.TurnOns);
        Assert.Equal("restored", status.Circuits.Single(c => c.EntityId == Breaker("small")).Status);
    }

    [Fact]
    public async Task OutageRechecksPowerThatRisesDuringCooldownAndResumesWhenItFalls()
    {
        RecoverySnapshot("grid", ("small", "on", 10, 5000), ("large", "on", 10, 6001));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("small", "off", 0, 5000), ("large", "off", 0, 6001));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        RecoverySnapshot("battery", ("small", "on", 0, 5000), ("large", "off", 0, 6001));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        RecoverySnapshot("battery", ("small", "on", 5000, 5000), ("large", "off", 0, 6001));
        await service.PollAsync(home.Id);
        Assert.Equal("capacityLimited", (await RecoveryStatus()).Status);
        Assert.Equal(1, provider.TurnOns);
        RecoverySnapshot("battery", ("small", "on", 100, 5000), ("large", "off", 0, 6001));
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.TurnOns);
    }

    [Fact]
    public async Task OutageUsesHistoricalPeakAndNeverTreatsUnknownEstimateAsZero()
    {
        RecoverySnapshot("grid", ("cycled", "on", 3000, null), ("unknown", "on", 0, null));
        var home = await RecoveryHome();
        RecoverySnapshot("grid", ("cycled", "on", 1, null), ("unknown", "on", 0, null));
        await service.PollAsync(home.Id);
        RecoverySnapshot("battery", ("cycled", "off", 0, null), ("unknown", "off", 0, null));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        RecoverySnapshot("battery", ("cycled", "on", 1, null), ("unknown", "off", 0, null));
        await service.PollAsync(home.Id);
        Assert.Equal(3000, (await RecoveryStatus()).Circuits.Single(c => c.EntityId == Breaker("cycled")).EstimatedWatts);
        Assert.Equal("waitingForEstimate", (await RecoveryStatus()).Status);
        Assert.Equal(1, provider.TurnOns);
    }

    [Fact]
    public async Task OutageIncludesManuallyAddedCircuitsAndFreshHouseholdLoadInBudget()
    {
        RecoverySnapshot("grid", ("restore", "on", 1001, 1001), ("extra", "off", 0, 10000));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("restore", "off", 0, 1001), ("extra", "on", 10000, 10000));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal("capacityLimited", (await RecoveryStatus()).Status);
        Assert.Equal(0, provider.TurnOns);
    }

    [Fact]
    public async Task OutagePausesForUnknownModeMissingMetersAndLabInitialization()
    {
        RecoverySnapshot("grid", ("a", "on", 100, 100));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("a", "off", 0, 100));
        provider.Snapshot = provider.Snapshot! with { BackupReady = false };
        await service.PollAsync(home.Id);
        clock.Advance(10);
        await service.PollAsync(home.Id);
        Assert.Equal("waitingForBackup", (await RecoveryStatus()).Status);
        RecoverySnapshot("unknown", ("a", "off", 0, 100));
        await service.PollAsync(home.Id);
        Assert.Equal("paused", (await RecoveryStatus()).Status);
        RecoverySnapshot("battery", ("a", "off", null, 100));
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal("waitingForTelemetry", (await RecoveryStatus()).Status);
        Assert.Equal(0, provider.TurnOns);
        RecoverySnapshot("battery", ("a", "off", 0, 100));
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.TurnOns);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
    }

    [Fact]
    public async Task OutageDoesNotInventBaselineWhenStartingOnBatteryOrAfterLongOfflineGap()
    {
        RecoverySnapshot("battery", ("a", "off", 0, 100));
        var home = await RecoveryHome();
        clock.Advance(10);
        await service.PollAsync(home.Id);
        Assert.Equal("waitingForBaseline", (await RecoveryStatus()).Status);
        RecoverySnapshot("grid", ("a", "on", 100, 100));
        await service.PollAsync(home.Id);
        clock.Advance(21);
        RecoverySnapshot("battery", ("a", "off", 0, 100));
        await service.PollAsync(home.Id);
        Assert.Equal("waitingForBaseline", (await RecoveryStatus()).Status);
        Assert.Equal(0, provider.TurnOns);
    }

    [Fact]
    public async Task OutageIntentSurvivesReloadAndUnconfirmedCommandBlocksFurtherRestores()
    {
        RecoverySnapshot("grid", ("a", "on", 100, 100), ("b", "on", 200, 200));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("a", "off", 0, 100), ("b", "off", 0, 200));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        provider.Fail = true;
        await service.PollAsync(home.Id);
        db.ChangeTracker.Clear();
        clock.Advance(10);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal("blocked", (await RecoveryStatus()).Status);
        clock.Advance(30);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
    }

    [Fact]
    public async Task OutageManualOffOverridesPendingRestoreAndGridRecoveryStopsRollout()
    {
        RecoverySnapshot("grid", ("a", "on", 100, 100), ("b", "on", 200, 200));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("a", "off", 0, 100), ("b", "off", 0, 200));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        await service.CircuitCommandAsync("alice", home.Id, new(Breaker("a"), "Off", "manual-override"));
        Assert.Equal("held", (await RecoveryStatus()).Circuits.Single(c => c.EntityId == Breaker("a")).Status);
        RecoverySnapshot("grid", ("a", "off", 0, 100), ("b", "off", 0, 200));
        await service.PollAsync(home.Id);
        clock.Advance(10);
        await service.PollAsync(home.Id);
        Assert.Equal("monitoring", (await RecoveryStatus()).Status);
        Assert.Equal(1, provider.TurnOns);
    }

    [Fact]
    public async Task OutageCancellationAndPermissionChangesDoNotRestartOrCancelUnrelatedRecovery()
    {
        RecoverySnapshot("grid", ("a", "on", 100, 100));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("a", "off", 0, 100));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        await service.SmartPowerOffAsync("alice", home.Id, new(false));
        await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total", new()));
        var cmd = (await service.HomesAsync("alice"))[0].Commands.Single();
        Assert.Equal("AwaitingConfirmation", cmd.Status);
        await service.CancelCommandAsync("alice", home.Id, cmd.Id);
        clock.Advance(10);
        await service.PollAsync(home.Id);
        Assert.Equal("held", (await RecoveryStatus()).Circuits.Single().Status);
        Assert.Equal(1, provider.TurnOns);
    }

    [Fact]
    public async Task OutageSchemaUpgradePreservesExistingHomesAndCommands()
    {
        RecoverySnapshot("grid", ("a", "on", 100, 100));
        var home = await RecoveryHome();
        await service.CircuitCommandAsync("alice", home.Id, new(Breaker("a"), "Off", "migration"));
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN OutageRecoveryJson");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN OutageEventId");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        var upgraded = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal(home.Id, upgraded.Id);
        Assert.Single(upgraded.Commands);
        Assert.Null(upgraded.Commands[0].OutageEventId);
    }

    [Fact]
    public async Task OutageWaitsAgainAfterReadFailureAndDoesNotRestoreAfterRevocation()
    {
        RecoverySnapshot("grid", ("a", "on", 100, 100));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("a", "off", 0, 100));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        provider.FailRead = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.PollAsync(home.Id));
        provider.FailRead = false;
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.TurnOns);
        await service.RevokeAsync("alice", home.Id);
        clock.Advance(10);
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.TurnOns);
    }

    [Fact]
    public async Task OutageAdmitsExactlyTheBudgetAndSettlesBeforeTheNextCircuit()
    {
        RecoverySnapshot("grid", ("kept", "on", 4000, 4000), ("restore", "on", 7000, 7000));
        var home = await RecoveryHome();
        RecoverySnapshot("battery", ("kept", "on", 4000, 4000), ("restore", "off", 0, 7000));
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        RecoverySnapshot("battery", ("kept", "on", 4000, 4000), ("restore", "on", 7000, 7000));
        await service.PollAsync(home.Id);
        Assert.Equal("complete", (await RecoveryStatus()).Status);
        Assert.Equal(11000, (await RecoveryStatus()).ReservedWatts);
    }
}
