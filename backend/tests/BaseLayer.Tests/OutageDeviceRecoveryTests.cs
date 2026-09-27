using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    [Fact]
    public async Task DeviceThatStayedOnThroughOutageIsNotRestartedAfterManualStop()
    {
        ApplianceSnapshot("grid", "on", "on");
        var home = await RecoveryHome();
        ApplianceSnapshot("battery", "on", "on");
        await service.PollAsync(home.Id);
        ApplianceSnapshot("battery", "on", "off");
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Empty(provider.DeviceRestores);
        Assert.Equal("held", (await RecoveryStatus()).Circuits.Single().Devices!.Single().Status);
    }
    private void ApplianceSnapshot(string mode, string relay, string lamp, string fan = "off", string climate = "off", bool supply = true)
    {
        RecoverySnapshot(mode, ("a", relay, relay == "on" ? 100 : 0, 3000));
        provider.Snapshot = provider.Snapshot! with
        {
            Devices = [..provider.Snapshot!.Devices, new("light.lamp", "Lamp", lamp), new("fan.room", "Fan", fan), new("climate.room", "Thermostat", climate)],
            CircuitDevices = new() { [Breaker("a")] = ["light.lamp", "fan.room", "climate.room"] },
            CircuitSupplyStates = new() { [Breaker("a")] = supply }
        };
    }

    [Fact]
    public async Task RestartsOnlyPreOutageDevicesAfterCircuitAndSupplyConfirmation()
    {
        ApplianceSnapshot("grid", "on", "on");
        var home = await RecoveryHome();
        ApplianceSnapshot("battery", "off", "off", supply: false);
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Empty(provider.DeviceRestores);
        ApplianceSnapshot("battery", "on", "off", supply: false);
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal("waitingForCircuitPower", (await RecoveryStatus()).Status);
        Assert.Empty(provider.DeviceRestores);
        ApplianceSnapshot("battery", "on", "off");
        await service.PollAsync(home.Id);
        Assert.Equal(("light.lamp", "on"), Assert.Single(provider.DeviceRestores));
        db.ChangeTracker.Clear();
        await service.PollAsync(home.Id);
        Assert.Single(provider.DeviceRestores);
        ApplianceSnapshot("battery", "on", "on");
        await service.PollAsync(home.Id);
        var status = await RecoveryStatus();
        Assert.Equal("complete", status.Status);
        Assert.Equal("restored", Assert.Single(status.Circuits.Single().Devices!).Status);
        Assert.DoesNotContain(provider.DeviceRestores, x => x.EntityId == "fan.room");
    }

    [Fact]
    public async Task RestoresSavedThermostatModeAndSerializesDevicesWithFiveSecondDelay()
    {
        ApplianceSnapshot("grid", "on", "on", climate: "cool");
        var home = await RecoveryHome();
        ApplianceSnapshot("battery", "on", "off");
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(("light.lamp", "on"), Assert.Single(provider.DeviceRestores));
        ApplianceSnapshot("battery", "on", "on");
        await service.PollAsync(home.Id);
        clock.Advance(4);
        await service.PollAsync(home.Id);
        Assert.Single(provider.DeviceRestores);
        clock.Advance(1);
        await service.PollAsync(home.Id);
        Assert.Equal(("climate.room", "cool"), provider.DeviceRestores[1]);
        ApplianceSnapshot("battery", "on", "on", climate: "cool");
        await service.PollAsync(home.Id);
        Assert.Equal("complete", (await RecoveryStatus()).Status);
    }

    [Fact]
    public async Task DeviceRecoveryPausesOnChangedMappingOrOverBudgetLoad()
    {
        ApplianceSnapshot("grid", "on", "on");
        var home = await RecoveryHome();
        ApplianceSnapshot("battery", "on", "off");
        await service.PollAsync(home.Id);
        clock.Advance(5);
        provider.Snapshot = provider.Snapshot! with { CircuitDevices = new() };
        await service.PollAsync(home.Id);
        Assert.Equal("waitingForDeviceMapping", (await RecoveryStatus()).Status);
        Assert.Empty(provider.DeviceRestores);
        ApplianceSnapshot("battery", "on", "off");
        provider.Snapshot!.Power["sensor.total"] = 11000;
        await service.PollAsync(home.Id);
        Assert.Equal("capacityLimited", (await RecoveryStatus()).Status);
        Assert.Empty(provider.DeviceRestores);
    }

    [Fact]
    public async Task CancelledOrManuallyStoppedDeviceIsNotStartedAgain()
    {
        ApplianceSnapshot("grid", "on", "on", "on");
        var home = await RecoveryHome();
        ApplianceSnapshot("battery", "on", "off");
        await service.PollAsync(home.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        var pending = (await service.HomesAsync("alice"))[0].Commands.Single();
        await service.CancelCommandAsync("alice", home.Id, pending.Id);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.DeviceRestores.Count);
        Assert.Equal("fan.room", provider.DeviceRestores[1].EntityId);
        ApplianceSnapshot("battery", "on", "off", "on");
        await service.PollAsync(home.Id);
        ApplianceSnapshot("battery", "on", "off", "off");
        clock.Advance(10);
        await service.PollAsync(home.Id);
        Assert.All((await RecoveryStatus()).Circuits.Single().Devices!, d => Assert.Equal("held", d.Status));
        Assert.Equal(2, provider.DeviceRestores.Count);
    }

    [Fact]
    public async Task UnconfirmedDeviceBlocksNextCircuitAndGridReturnCancelsIt()
    {
        ApplianceSnapshot("grid", "on", "on");
        var home = await RecoveryHome();
        ApplianceSnapshot("battery", "on", "off");
        await service.PollAsync(home.Id);
        clock.Advance(5);
        provider.Fail = true;
        await service.PollAsync(home.Id);
        clock.Advance(15);
        await service.PollAsync(home.Id);
        Assert.Equal("blocked", (await RecoveryStatus()).Status);
        Assert.Single(provider.DeviceRestores);
        ApplianceSnapshot("grid", "on", "off");
        await service.PollAsync(home.Id);
        Assert.Equal("monitoring", (await RecoveryStatus()).Status);
    }
}
