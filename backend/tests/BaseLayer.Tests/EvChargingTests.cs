using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    private static ProviderSnapshot EvSnapshot(double? total = 13680, double? amps = 32, double? power = 7680, string state = "on") => new(
        [new("switch.ev", "EV charger", state)],
        [new("sensor.total", "Total", "W"), new("sensor.ev", "EV", "W")],
        new() { ["sensor.total"] = total, ["sensor.ev"] = power },
        [new("input_number.ev_current", "EV current", amps, 6, 48, 1)]);
    private async Task<HomeDto> EvHome(string level = "Anytime", bool enabled = true)
    {
        provider.Snapshot = EvSnapshot();
        var home = await Connect();
        return await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total",
            new() { ["switch.ev"] = "sensor.ev" }, ShutoffLevels: new() { ["switch.ev"] = level },
            SmartPowerOffEnabled: enabled, EvCharging: new() { ["switch.ev"] = new("input_number.ev_current") }));
    }
    [Fact]
    public async Task EvThrottlesAndKeepsChargingWithObservedConfirmation()
    {
        var home = await EvHome();
        await service.PollAsync(home.Id);
        Assert.Equal(("input_number.ev_current", 20d), Assert.Single(provider.CurrentSends));
        Assert.Equal(0, provider.Sends);
        var sent = Assert.Single((await service.HomesAsync("alice"))[0].Commands);
        Assert.Equal("SetCurrent", sent.Action);
        Assert.Equal("AwaitingConfirmation", sent.Status);
        await service.PollAsync(home.Id); // Unchanged observations do not confirm or resend.
        Assert.Single(provider.CurrentSends);
        provider.Snapshot = EvSnapshot(10800, 20, 4800);
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal("Confirmed", Assert.Single(home.Commands).Status);
        Assert.Equal("on", Assert.Single(home.Devices).State);
        Assert.Equal(32, Assert.Single(home.RestoreQueue!).TargetAmps);
        Assert.Equal("monitoring", home.SmartPowerOffStatus);
    }
    [Theory]
    [InlineData("Never", true)]
    [InlineData("Sometimes", true)]
    [InlineData("Anytime", false)]
    public async Task EvAutomationRespectsPermissions(string level, bool enabled)
    {
        var home = await EvHome(level, enabled);
        await service.PollAsync(home.Id);
        Assert.Empty(provider.CurrentSends);
        Assert.Equal(0, provider.Sends);
    }
    [Theory]
    [InlineData(18000d, 32d, 7680d)] // Remaining headroom is below the 6 A minimum.
    [InlineData(13000d, null, 7680d)]
    [InlineData(13000d, 32d, null)]
    public async Task EvFallsBackToShutoffWhenCurrentCannotBeReduced(double total, double? amps, double? watts)
    {
        var home = await EvHome();
        provider.Snapshot = EvSnapshot(total, amps, watts);
        await service.PollAsync(home.Id);
        Assert.Empty(provider.CurrentSends);
        // Unknown device power cannot be included in measured shutoff recommendations.
        Assert.Equal(watts is null ? 0 : 1, provider.Sends);
    }
    [Theory]
    [InlineData(null)]
    [InlineData(10000d)]
    public async Task EvDoesNotActWithoutAnOverload(double? total)
    {
        var home = await EvHome();
        provider.Snapshot = EvSnapshot(total);
        await service.PollAsync(home.Id);
        Assert.Empty(provider.CurrentSends);
        Assert.Equal(0, provider.Sends);
    }
    [Fact]
    public async Task EvFailedReductionFallsBackInsteadOfRecreatingIt()
    {
        var home = await EvHome();
        provider.Fail = true;
        await service.PollAsync(home.Id);
        clock.Advance(31);
        provider.Fail = false;
        await service.PollAsync(home.Id);
        Assert.Single(provider.CurrentSends);
        Assert.Equal(1, provider.Sends);
    }
    [Fact]
    public async Task EvManualCurrentValidatesOwnershipStepsAndIdempotency()
    {
        var home = await EvHome("Sometimes", false);
        await service.PollAsync(home.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.EvCurrentAsync("bob", home.Id, new("switch.ev", 16, "other")));
        foreach (var value in new[] { 5, 49, 16.5, double.NaN })
            await Assert.ThrowsAsync<ArgumentException>(() => service.EvCurrentAsync("alice", home.Id, new("switch.ev", value, "invalid")));
        var command = await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "manual"));
        Assert.Equal(command.Id, (await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "manual"))).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.EvCurrentAsync("alice", home.Id, new("switch.ev", 17, "manual")));
        await service.PollAsync(home.Id);
        Assert.Equal(16, Assert.Single(provider.CurrentSends).Amps);
        provider.Snapshot = EvSnapshot(9840, 16, 3840);
        await service.PollAsync(home.Id);
        Assert.Equal("Confirmed", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
    }
    [Fact]
    public async Task EvPermissionAndMappingChangesCancelPendingCurrent()
    {
        var home = await EvHome();
        await service.PollAsync(home.Id);
        await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total", new() { ["switch.ev"] = "sensor.ev" },
            ShutoffLevels: new() { ["switch.ev"] = "Sometimes" }));
        clock.Advance(20);
        await service.PollAsync(home.Id);
        Assert.Single(provider.CurrentSends);
        Assert.Equal("Cancelled", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total", new(),
            EvCharging: new() { ["switch.ev"] = new("number.unmapped") })));
    }
    [Fact]
    public async Task EvSchemaUpgradePreservesExistingHomeWithoutOptingIn()
    {
        var home = await EvHome();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN CurrentControlsJson");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN EvCurrentEntityId");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN EvWattsPerAmp");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN CurrentControlEntityId");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN CurrentAmps");
        await BaseLayer.Data.DatabaseInitializer.InitializeAsync(db);
        await BaseLayer.Data.DatabaseInitializer.InitializeAsync(db);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Null(Assert.Single(home.Devices).EvCharging);
        Assert.Equal("sensor.ev", Assert.Single(home.Devices).PowerSensorId);
    }
    [Fact]
    public async Task EvRestoreUsesRaisedCurrentLimitInsteadOfOldPowerDraw()
    {
        var home = await EvHome();
        provider.Snapshot = EvSnapshot(18000); // Minimum charging cannot fit.
        await service.PollAsync(home.Id);
        provider.Snapshot = EvSnapshot(4000, 32, 0, "off");
        await service.PollAsync(home.Id);
        var queued = Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!);
        Assert.Equal(7680, queued.EstimatedWatts);
        provider.Snapshot = EvSnapshot(4000, 48, 0, "off");
        await service.PollAsync(home.Id);
        Assert.Equal(11520, Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!).EstimatedWatts);
        clock.Advance(180);
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.TurnOns);
    }
    [Fact]
    public async Task EvUnconfirmedReductionExpiresAndFallsBackEvenAfterServiceSuccess()
    {
        var home = await EvHome();
        await service.PollAsync(home.Id);
        clock.Advance(31);
        await service.PollAsync(home.Id);
        Assert.Single(provider.CurrentSends);
        Assert.Equal(1, provider.Sends);
    }
    [Fact]
    public async Task EvFurtherLoadIncreaseReplansFromMeasuredUsage()
    {
        var home = await EvHome();
        await service.PollAsync(home.Id); // 20 A.
        provider.Snapshot = EvSnapshot(11800, 20, 4800); // Other load increased by 1 kW.
        await service.PollAsync(home.Id); // Confirm observed current first.
        await service.PollAsync(home.Id); // Fresh reading, another reduction.
        Assert.Equal(16, provider.CurrentSends.Last().Amps);
        Assert.Equal(0, provider.Sends);
    }
    [Theory]
    [InlineData(240, 1, 20)]
    [InlineData(240, 2, 20)]
    [InlineData(690, 1, 7)]
    public void EvCalculationUsesElectricalRatingAndRoundsDown(double wattsPerAmp, double step, double expected)
    {
        var home = new Home { SmartPowerOffEnabled = true, HouseholdWatts = 13680 };
        var device = new Device { Present = true, Allowed = true, State = "on", ShutoffLevel = "Anytime", PowerWatts = 7680,
            EvCurrentEntityId = "number.ev", EvWattsPerAmp = wattsPerAmp };
        var result = EvChargingPolicy.Reduction(home, device, new("number.ev", "EV", 32, 6, 48, step), 11000);
        Assert.Equal(expected, result);
        Assert.True(6000 + result * wattsPerAmp < 11000);
    }
}
