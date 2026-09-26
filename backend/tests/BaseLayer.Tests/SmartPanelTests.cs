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
        device.RestoreWatts = 100;
        Assert.False(AutoRestorePolicy.Fits(new Home { SmartPowerOffEnabled = true, HouseholdWatts = 500 }, device, 11000));
    }
}
