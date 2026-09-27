using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    [Fact]
    public async Task AlwaysBelowLimitPersistsAndReducesAtLowRiskWithoutChangingRiskOrPermissions()
    {
        gridRisk.Risk = GridOutageRisk.Low;
        var home = await SmartHome(23000);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SmartPowerOffAsync("bob", home.Id, new(true, true)));
        await service.SmartPowerOffAsync("alice", home.Id, new(true, true));
        db.ChangeTracker.Clear();
        Assert.True(Assert.Single(await service.HomesAsync("alice")).AlwaysKeepBelowBatteryLimit);
        await service.PollAsync(home.Id);
        Assert.Equal(3, provider.Sends);
        provider.Snapshot = SmartSnapshot(16000, "switch.large", "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        var result = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal("low", result.GridOutageRisk);
        Assert.Equal("review", result.SmartPowerOffStatus);
        Assert.All(result.Commands, c => Assert.Equal("Anytime", result.Devices.Single(d => d.EntityId == c.EntityId).ShutoffLevel));
        Assert.All(result.Devices.Where(d => d.Recommended), d => Assert.Equal("Sometimes", d.ShutoffLevel));
        Assert.Equal(3, provider.Sends);
    }

    [Fact]
    public async Task AlwaysBelowLimitCanBeSavedWithDeviceSettingsAndPreservedByOlderRequests()
    {
        var home = await SmartHome(enabled: false);
        await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total", new(),
            SmartPowerOffEnabled: true, AlwaysKeepBelowBatteryLimit: true));
        await service.SmartPowerOffAsync("alice", home.Id, new(false));
        db.ChangeTracker.Clear();
        var saved = Assert.Single(await service.HomesAsync("alice"));
        Assert.True(saved.AlwaysKeepBelowBatteryLimit);
        Assert.False(saved.SmartPowerOffEnabled);
    }

    [Fact]
    public async Task AlwaysBelowLimitStopsLowRiskRetriesWhenTurnedOff()
    {
        gridRisk.Risk = GridOutageRisk.Low;
        var home = await SmartHome();
        await service.SmartPowerOffAsync("alice", home.Id, new(true, true));
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        await service.SmartPowerOffAsync("alice", home.Id, new(true, false));
        await PollFor(home.Id, 20);
        Assert.Equal(1, provider.Sends);
        Assert.Equal("Cancelled", Assert.Single(Assert.Single(await service.HomesAsync("alice")).Commands).Status);
    }

    [Fact]
    public async Task AlwaysBelowLimitDoesNotRestoreLoadsAboveLimitAtLowRisk()
    {
        var home = await QueueLoads();
        await service.SmartPowerOffAsync("alice", home.Id, new(true, true));
        gridRisk.Risk = GridOutageRisk.Low;
        provider.Snapshot = SmartSnapshot(10500, "switch.large", "switch.medium", "switch.small");
        await PollFor(home.Id, 20);
        Assert.Equal(0, provider.TurnOns);
        provider.Snapshot = SmartSnapshot(9000, "switch.large", "switch.medium", "switch.small");
        await PollFor(home.Id, 6);
        Assert.Equal(1, provider.TurnOns);
        Assert.Equal("switch.small", Assert.Single(Assert.Single(await service.HomesAsync("alice")).Commands, c => c.Action == "On").EntityId);
    }

    [Fact]
    public async Task AlwaysBelowLimitThrottlesEvAndBudgetsItsRestorationAtLowRisk()
    {
        gridRisk.Risk = GridOutageRisk.Low;
        var home = await EvHome();
        await service.SmartPowerOffAsync("alice", home.Id, new(true, true));
        await service.PollAsync(home.Id);
        Assert.Equal(20, Assert.Single(provider.CurrentSends).Amps);
        provider.Snapshot = EvSnapshot(10800, 20, 4800);
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 20);
        Assert.Single(provider.CurrentSends);
        provider.Snapshot = EvSnapshot(8800, 20, 4800);
        await PollFor(home.Id, 6);
        Assert.Equal(27, provider.CurrentSends.Last().Amps);
    }

    [Fact]
    public async Task AlwaysBelowLimitMigrationDefaultsOffAndIsIdempotent()
    {
        var home = await SmartHome();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN AlwaysKeepBelowBatteryLimit");
        await BaseLayer.Data.DatabaseInitializer.InitializeAsync(db);
        await BaseLayer.Data.DatabaseInitializer.InitializeAsync(db);
        var saved = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal(home.Id, saved.Id);
        Assert.False(saved.AlwaysKeepBelowBatteryLimit);
        Assert.True(saved.SmartPowerOffEnabled);
    }
}
