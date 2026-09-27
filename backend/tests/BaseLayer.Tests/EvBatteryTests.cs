using BaseLayer.Application.Contracts;
using BaseLayer.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    [Fact]
    public async Task EvBatteryIsDetectedWithoutSetupAndTracksCapacityAndMissingReadings()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = EvSnapshot() with { EvBatterySensors = [new("sensor.ev_battery", "EV battery", 42, 75), new("sensor.home_battery", "Home battery", 80, 13.5)] };
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        var battery = Assert.Single(home.Devices).EvBattery!;
        Assert.True(battery.AutoDetected);
        Assert.Equal("sensor.ev_battery", battery.SensorEntityId);
        Assert.Equal(75, battery.CapacityKwh);
        Assert.Empty(provider.CurrentSends);
        provider.Snapshot = EvSnapshot() with { EvBatterySensors = [new("sensor.ev_battery", "EV battery", null, 80)] };
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal(80, Assert.Single(home.Devices).EvBattery!.CapacityKwh);
        Assert.Null(Assert.Single(home.EvBatterySensors!).Percent);
        provider.Snapshot = EvSnapshot();
        await service.PollAsync(home.Id);
        Assert.Null(Assert.Single((await service.HomesAsync("alice"))[0].Devices).EvBattery);
    }

    [Fact]
    public async Task EvBatteryAmbiguityRequiresSelectionAndManualSettingsTakePrecedence()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = EvSnapshot() with { EvBatterySensors = [new("sensor.ev_battery", "EV battery", 42, 75), new("sensor.ev_soc", "EV charge", 43, 80)] };
        await service.PollAsync(home.Id);
        Assert.Null(Assert.Single((await service.HomesAsync("alice"))[0].Devices).EvBattery);
        home = await service.EvBatterySettingsAsync("alice", home.Id, new("switch.ev", new("sensor.ev_soc", 78, 92, AutoDetected: true)));
        var battery = Assert.Single(home.Devices).EvBattery!;
        Assert.False(battery.AutoDetected);
        Assert.Equal("sensor.ev_soc", battery.SensorEntityId);
        Assert.Equal(78, battery.CapacityKwh);
        provider.Snapshot = EvSnapshot() with { EvBatterySensors = [new("sensor.ev_battery", "EV battery", 42, 75)] };
        await service.PollAsync(home.Id);
        Assert.Equal("sensor.ev_soc", Assert.Single((await service.HomesAsync("alice"))[0].Devices).EvBattery!.SensorEntityId);
        home = await service.EvBatterySettingsAsync("alice", home.Id, new("switch.ev", null));
        Assert.True(Assert.Single(home.Devices).EvBattery!.AutoDetected);
        Assert.Equal("sensor.ev_battery", Assert.Single(home.Devices).EvBattery!.SensorEntityId);
    }

    [Fact]
    public void EvBatteryMatchesLabIdentityAndNeverSharesOneSensorAcrossChargers()
    {
        Assert.True(BaseLayer.Application.Services.EvBatteryDiscovery.Matches("switch.virtual_ev_charger", "Virtual EV Charger", "sensor.virtual_ev_battery_charge", "Virtual EV Battery Charge"));
        Assert.True(BaseLayer.Application.Services.EvBatteryDiscovery.Matches("sensor.virtual_ev_battery_charge", "Virtual EV Battery Charge", "input_number.lab_ev_capacity", "EV usable battery capacity"));
        Assert.False(BaseLayer.Application.Services.EvBatteryDiscovery.Matches("switch.ev1_charger", "Car one", "sensor.ev2_battery", "Car two"));
        var detected = BaseLayer.Application.Services.EvBatteryDiscovery.Resolve([
            new() { EntityId = "switch.ev_charger", Name = "EV Charger", Present = true, EvCurrentEntityId = "number.one" },
            new() { EntityId = "switch.ev_charging", Name = "EV charging", Present = true, EvCurrentEntityId = "number.two" }
        ], [new("sensor.ev_battery", "EV battery", 50, 75)], _ => null);
        Assert.Empty(detected);
    }

    [Fact]
    public async Task EvBatteryPairingPersistsAndReadsLiveValuesWithoutChangingCharger()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = EvSnapshot() with { EvBatterySensors = [new("sensor.ev_battery", "Car battery", 42)] };
        await service.PollAsync(home.Id);
        home = await service.EvBatterySettingsAsync("alice", home.Id, new("switch.ev", new("sensor.ev_battery", 75)));
        Assert.Equal(75, Assert.Single(home.Devices).EvBattery!.CapacityKwh);
        Assert.Equal(42, Assert.Single(home.EvBatterySensors!).Percent);
        db.ChangeTracker.Clear();
        Assert.Equal("sensor.ev_battery", Assert.Single((await service.HomesAsync("alice"))[0].Devices).EvBattery!.SensorEntityId);
        provider.Snapshot = EvSnapshot() with { EvBatterySensors = [new("sensor.ev_battery", "Car battery", 65)] };
        await service.PollAsync(home.Id);
        Assert.Equal(65, Assert.Single((await service.HomesAsync("alice"))[0].EvBatterySensors!).Percent);
        clock.Advance(21);
        Assert.Null(Assert.Single((await service.HomesAsync("alice"))[0].EvBatterySensors!).Percent);
        Assert.Empty(provider.CurrentSends);
    }

    [Fact]
    public async Task EvBatteryPairingRejectsOtherOwnersAndInvalidInputs()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = EvSnapshot() with { EvBatterySensors = [new("sensor.ev_battery", "Car battery", 42)] };
        await service.PollAsync(home.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.EvBatterySettingsAsync("bob", home.Id, new("switch.ev", new("sensor.ev_battery", 75))));
        foreach (var setting in new[] { new EvBatterySettings("sensor.unknown", 75), new("sensor.ev_battery", 0), new("sensor.ev_battery", double.NaN), new("sensor.ev_battery", 75, 101), new("sensor.ev_battery", 75, 0) })
            await Assert.ThrowsAsync<ArgumentException>(() => service.EvBatterySettingsAsync("alice", home.Id, new("switch.ev", setting)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EvBatterySettingsAsync("alice", home.Id, new("switch.missing", new("sensor.ev_battery", 75))));
    }

    [Fact]
    public async Task EvBatteryCanBeAssignedAndCurrentSetWhileOffWithoutStartingCharging()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = EvSnapshot(4000, 32, 3, "off") with { EvBatterySensors = [new("sensor.ev_battery", "EV battery", 45, 75)] };
        await service.PollAsync(home.Id);
        home = await service.EvBatterySettingsAsync("alice", home.Id, new("switch.ev", new("sensor.ev_battery", 75)));
        Assert.Equal(45, Assert.Single(home.EvBatterySensors!).Percent);
        var command = await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "off-charger-rate"));
        await service.PollAsync(home.Id);
        Assert.Equal(16, Assert.Single(provider.CurrentSends).Amps);
        Assert.Equal(0, provider.TurnOns);
        provider.Snapshot = EvSnapshot(4000, 16, 3, "off") with { EvBatterySensors = [new("sensor.ev_battery", "EV battery", 45, 75)] };
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal("off", Assert.Single(home.Devices).State);
        Assert.Equal("Confirmed", home.Commands.Single(c => c.Id == command.Id).Status);
        Assert.Equal(0, provider.TurnOns);
    }

    [Fact]
    public async Task EvBatteryMigrationIsRepeatableAndPreservesExistingPairing()
    {
        var home = await EvHome("Sometimes", false);
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN EvBatterySensorsJson");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN EvBatterySettingsJson");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Null(Assert.Single(home.Devices).EvBattery);
        Assert.NotNull(Assert.Single(home.Devices).EvCharging);
        Assert.Empty(home.EvBatterySensors!);
    }
}
