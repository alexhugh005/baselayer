using BaseLayer.Application.Contracts;
using BaseLayer.Application.Services;
using BaseLayer.Domain.Entities;
using BaseLayer.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    private const string CircuitId = "switch.span_panel_living_room_breaker";
    private void CircuitState(string state) => provider.Snapshot = new([new(CircuitId, "SPAN Panel Living Room Breaker", state)], [], new());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BatteryCircuitDoesNotConfirmTemporaryRelayClosure(bool overBudget)
    {
        void Snapshot(string relay, double watts)
        {
            RecoverySnapshot("battery", ("load", "on", watts, 6000), ("living_room", relay, 0, 6000));
            provider.Snapshot = provider.Snapshot! with { CircuitSupplyStates = new() { [CircuitId] = false } };
        }
        Snapshot("off", 1000);
        var home = await Connect();
        await service.CircuitCommandAsync("alice", home.Id, new(CircuitId, "On", "temporary-on"));
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        // Native relay closes briefly, but the backup controller never admits supply.
        clock.Advance(1);
        Snapshot("on", 1000);
        await service.PollAsync(home.Id);
        Assert.Equal("AwaitingConfirmation", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
        clock.Advance(10);
        Snapshot("off", overBudget ? 6000 : 1000);
        await service.PollAsync(home.Id);
        var result = Assert.Single((await service.HomesAsync("alice"))[0].Commands);
        Assert.Equal("Failed", result.Status);
        Assert.Equal(overBudget ? "Kept off: would exceed the 11 kW battery limit." : "Stayed off: battery backup did not supply this circuit.", result.Message);
        Assert.Equal(1, provider.TurnOns);
    }

    [Fact]
    public async Task BatteryCircuitConfirmsOnlyAfterSupplyAndSettling()
    {
        RecoverySnapshot("battery", ("living_room", "off", 0, 100));
        provider.Snapshot = provider.Snapshot! with { CircuitSupplyStates = new() { [CircuitId] = false } };
        var home = await Connect();
        await service.CircuitCommandAsync("alice", home.Id, new(CircuitId, "On", "admitted"));
        await service.PollAsync(home.Id);
        RecoverySnapshot("battery", ("living_room", "on", 100, 100));
        provider.Snapshot = provider.Snapshot! with { CircuitSupplyStates = new() { [CircuitId] = true } };
        clock.Advance(1);
        await service.PollAsync(home.Id);
        Assert.Equal("AwaitingConfirmation", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal("Confirmed", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
        Assert.Equal(1, provider.TurnOns);
    }

    [Theory]
    [InlineData("battery", 6000d, 6000d, true)]
    [InlineData("battery", 5000d, 6000d, false)]
    [InlineData("grid", 6000d, 6000d, false)]
    [InlineData("unknown", 6000d, 6000d, false)]
    [InlineData("battery", 6000d, null, false)]
    [InlineData("battery", null, 6000d, false)]
    public async Task ManualCircuitReportsBatteryLimitOnlyWithKnownOverBudgetLoad(string mode, double? watts, double? estimate, bool blocked)
    {
        RecoverySnapshot(mode, ("load", "on", watts, 6000), ("living_room", "off", 0, estimate));
        var home = await Connect();
        await service.CircuitCommandAsync("alice", home.Id, new(CircuitId, "On", "battery-limit"));
        await service.PollAsync(home.Id);
        var result = Assert.Single((await service.HomesAsync("alice"))[0].Commands);
        Assert.Equal(blocked ? "Failed" : "AwaitingConfirmation", result.Status);
        Assert.Equal(blocked ? 0 : 1, provider.TurnOns);
        if (blocked)
        {
            Assert.Equal("Kept off: would exceed the 11 kW battery limit.", result.Message);
            // Reducing usage permits a fresh user attempt; the refusal is not retried automatically.
            RecoverySnapshot("battery", ("load", "on", 4000, 6000), ("living_room", "off", 0, 6000));
            await service.PollAsync(home.Id);
            Assert.Equal(0, provider.TurnOns);
            await service.CircuitCommandAsync("alice", home.Id, new(CircuitId, "On", "battery-retry"));
            await service.PollAsync(home.Id);
            Assert.Equal(1, provider.TurnOns);
        }
    }

    [Fact]
    public async Task BatteryLimitDoesNotBlockManualCircuitTurnOff()
    {
        RecoverySnapshot("battery", ("living_room", "on", 12000, 12000));
        var home = await Connect();
        await service.CircuitCommandAsync("alice", home.Id, new(CircuitId, "Off", "battery-off"));
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        Assert.Equal("AwaitingConfirmation", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
    }

    [Theory]
    [InlineData("On", "off", "on")]
    [InlineData("Off", "on", "off")]
    public async Task ManualCircuitUsesProviderAndWaitsForObservedConfirmation(string action, string before, string after)
    {
        CircuitState(before);
        var home = await Connect();
        Assert.True(home.Devices.Single().IsCircuit);
        Assert.False(home.Devices.Single().Allowed);
        var request = new CircuitCommandRequest(CircuitId, action, "panel-test");
        var command = await service.CircuitCommandAsync("alice", home.Id, request);
        Assert.Equal(command.Id, (await service.CircuitCommandAsync("alice", home.Id, request)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitCommandAsync("alice", home.Id, request with { Action = action == "On" ? "Off" : "On" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitCommandAsync("alice", home.Id, request with { IdempotencyKey = "duplicate" }));
        await service.PollAsync(home.Id);
        Assert.Equal(action == "On" ? 1 : 0, provider.TurnOns);
        Assert.Equal(action == "Off" ? 1 : 0, provider.Sends);
        Assert.Equal("AwaitingConfirmation", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
        CircuitState(after);
        await service.PollAsync(home.Id);
        var observed = (await service.HomesAsync("alice"))[0];
        Assert.Equal("Confirmed", observed.Commands.Single().Status);
        Assert.Equal(after, observed.Devices.Single().State);
        Assert.Empty(observed.RestoreQueue!);
    }

    [Fact]
    public async Task CircuitControlRequiresOwnedFreshAvailableDiscoveredCircuit()
    {
        CircuitState("on");
        var home = await Connect();
        var request = new CircuitCommandRequest(CircuitId, "Off", "access");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CircuitCommandAsync("bob", home.Id, request));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitCommandAsync("alice", home.Id, request with { EntityId = "switch.span_panel_unknown_breaker" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitCommandAsync("alice", home.Id, request with { EntityId = "switch.dryer" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitCommandAsync("alice", home.Id, request with { Action = "toggle" }));
        CircuitState("unavailable");
        await service.PollAsync(home.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitCommandAsync("alice", home.Id, request));
        CircuitState("on");
        await service.PollAsync(home.Id);
        clock.Advance(21);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitCommandAsync("alice", home.Id, request));
        await service.PollAsync(home.Id);
        await service.RevokeAsync("alice", home.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitCommandAsync("alice", home.Id, request));
        Assert.Equal(0, provider.Sends);
    }

    [Fact]
    public async Task CircuitFailuresRetryAndRemovedCircuitCancels()
    {
        CircuitState("on");
        var home = await Connect();
        await service.CircuitCommandAsync("alice", home.Id, new(CircuitId, "Off", "failure"));
        provider.Fail = true;
        await service.PollAsync(home.Id);
        Assert.Equal("Retrying", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
        provider.Snapshot = new([], [], new());
        clock.Advance(6);
        await service.PollAsync(home.Id);
        Assert.Equal("Cancelled", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
        Assert.Equal(1, provider.Sends);
    }

    [Fact]
    public async Task CircuitCommandsSurviveSettingsSaveAndSchemaUpgrade()
    {
        CircuitState("on");
        var home = await Connect();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN ManualCircuit");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        await service.CircuitCommandAsync("alice", home.Id, new(CircuitId, "Off", "settings"));
        await service.SettingsAsync("alice", home.Id, new(false, false, [], null, new()));
        db.ChangeTracker.Clear();
        Assert.True((await db.Set<DeviceCommand>().SingleAsync()).ManualCircuit);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
    }

    [Fact]
    public void PanelBreakersAreExcludedFromApplianceAutomation()
    {
        var device = new Device { EntityId = CircuitId, Allowed = true, Present = true, State = "on", ShutoffLevel = "Anytime", PowerWatts = 5000 };
        Assert.False(SmartUsagePolicy.Controllable(device));
        Assert.Empty(new UsageLimitReachedService().RecommendActions(13000, 11000, [device]));
        device.State = "off";
        device.RestoreEntry = new() { DeviceId = device.Id, EstimatedWatts = 100, PowerOn = true };
        Assert.False(AutoRestorePolicy.Fits(new Home { SmartPowerOffEnabled = true, HouseholdWatts = 500 }, device, 11000));
    }
}
