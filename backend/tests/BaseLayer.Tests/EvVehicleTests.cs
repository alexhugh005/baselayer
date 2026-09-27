using BaseLayer.Application.Contracts;
using BaseLayer.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    [Fact]
    public async Task MultipleVehiclesShareAChargerWithoutOverwritingTheExistingVehicle()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot();
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        var original = Assert.Single(home.EvVehicles!);
        var second = new EvVehicleDto(Guid.NewGuid().ToString(), "Family car", "switch.ev", new("sensor.ev_battery", 60, 92));
        home = await service.SaveEvVehicleAsync("alice", home.Id, second);
        Assert.Equal(2, home.EvVehicles!.Count);
        Assert.Equal(original, home.EvVehicles!.Single(v => v.Id == original.Id));
        home = await service.SaveEvVehicleAsync("alice", home.Id, second with { Name = "Commuter", Battery = second.Battery with { CapacityKwh = 65 } });
        Assert.Equal(2, home.EvVehicles!.Count);
        Assert.Equal(75, home.EvVehicles!.Single(v => v.Id == original.Id).Battery.CapacityKwh);
        db.ChangeTracker.Clear();
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal("Commuter", home.EvVehicles!.Single(v => v.Id == second.Id).Name);
        Assert.Equal(65, home.EvVehicles!.Single(v => v.Id == second.Id).Battery.CapacityKwh);
        Assert.Empty(provider.CurrentSends);
        Assert.Empty(provider.ChargeLimitSends);
    }

    [Fact]
    public async Task SelectedVehicleUsesItsOwnChargeTargetAndEditsCancelPendingCommands()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot() with { EvBatterySensors = [
            new("sensor.ev_battery", "EV battery", 50, 75, new("number.first_target", "Target one", 100, 50, 100, 1)),
            new("sensor.second_battery", "Second battery", 30, 60, new("number.second_target", "Target two", 90, 50, 100, 1))] };
        await service.PollAsync(home.Id);
        var vehicle = new EvVehicleDto(Guid.NewGuid().ToString(), "Second car", "switch.ev", new("sensor.second_battery", 60));
        await service.SaveEvVehicleAsync("alice", home.Id, vehicle);
        await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "second", 80, vehicle.Id));
        await service.PollAsync(home.Id);
        Assert.Equal(("number.second_target", 80d), Assert.Single(provider.ChargeLimitSends));
        Assert.Empty(provider.CurrentSends);
        home = await service.SaveEvVehicleAsync("alice", home.Id, vehicle with { Battery = vehicle.Battery with { CapacityKwh = 70 } });
        Assert.Equal("Cancelled", Assert.Single(home.Commands).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "second", 80, "switch.ev")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "missing", 80, "other-home-vehicle")));
    }

    [Fact]
    public async Task VehicleSetupValidatesOwnershipAndAllowsUnavailableBatterySensors()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot() with { EvBatterySensors = [new("sensor.ev_battery", "Battery", null, 75)] };
        await service.PollAsync(home.Id);
        var vehicle = new EvVehicleDto(Guid.NewGuid().ToString(), "Car", "switch.ev", new("sensor.ev_battery", 75));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SaveEvVehicleAsync("bob", home.Id, vehicle));
        foreach (var invalid in new[] { vehicle with { Name = " " }, vehicle with { ChargerEntityId = "switch.missing" },
            vehicle with { Battery = vehicle.Battery with { SensorEntityId = "sensor.missing" } },
            vehicle with { Battery = vehicle.Battery with { CapacityKwh = double.NaN } } })
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveEvVehicleAsync("alice", home.Id, invalid));
        home = await service.SaveEvVehicleAsync("alice", home.Id, vehicle);
        Assert.Contains(home.EvVehicles!, v => v.Id == vehicle.Id);
        Assert.Equal(0, provider.TurnOns);
    }

    [Fact]
    public async Task VehicleSchemaUpgradeKeepsExistingPairings()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot();
        await service.PollAsync(home.Id);
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN EvVehiclesJson");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN EvVehicleId");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal("sensor.ev_battery", Assert.Single(home.EvVehicles!).Battery.SensorEntityId);
        Assert.NotNull(Assert.Single(home.Devices).EvCharging);
    }
}
