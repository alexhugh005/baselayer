using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;
using BaseLayer.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    private const string PriorityId = "select.span_panel_living_room_circuit_priority";
    private void PriorityState(string? priority) => provider.Snapshot = new(
        [new(CircuitId, "SPAN Panel Living Room Breaker", "on")], [], new(),
        CircuitPriorities: [new(PriorityId, priority, ["never", "soc_threshold", "off_grid"])]);

    [Theory]
    [InlineData("never")]
    [InlineData("soc_threshold")]
    public async Task OutageSettingChangesPanelPriorityOnlyAndConfirmsObservedValue(string target)
    {
        PriorityState("off_grid");
        var home = await Connect();
        Assert.Equal("off_grid", home.Devices.Single().CircuitPriority!.Priority);
        var request = new CircuitPriorityRequest(CircuitId, target, "priority");
        var command = await service.CircuitPriorityAsync("alice", home.Id, request);
        Assert.Equal(command.Id, (await service.CircuitPriorityAsync("alice", home.Id, request)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitPriorityAsync("alice", home.Id, request with { Priority = "off_grid" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitCommandAsync("alice", home.Id, new(CircuitId, "Off", "other")));
        await service.SettingsAsync("alice", home.Id, new(false, false, [], null, new()));
        db.ChangeTracker.Clear(); // Both requested and observed settings survive a reload.
        await service.PollAsync(home.Id);
        Assert.Equal((PriorityId, target), Assert.Single(provider.PrioritySends));
        var pending = (await service.HomesAsync("alice"))[0];
        Assert.Equal("AwaitingConfirmation", pending.Commands.Single().Status);
        Assert.Equal(target, pending.Commands.Single().CircuitPriority);
        Assert.Equal("off_grid", pending.Devices.Single().CircuitPriority!.Priority);
        PriorityState(target);
        await service.PollAsync(home.Id);
        var confirmed = (await service.HomesAsync("alice"))[0];
        Assert.Equal("Confirmed", confirmed.Commands.Single().Status);
        Assert.Equal(target, confirmed.Devices.Single().CircuitPriority!.Priority);
        Assert.Equal("on", confirmed.Devices.Single().State);
        Assert.Equal(0, provider.Sends + provider.TurnOns);
        Assert.Empty(confirmed.RestoreQueue!);
    }

    [Fact]
    public async Task OutageSettingRejectsUnownedStaleUnavailableAndUndiscoveredControls()
    {
        PriorityState("off_grid");
        var home = await Connect();
        var request = new CircuitPriorityRequest(CircuitId, "never", "priority");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CircuitPriorityAsync("bob", home.Id, request));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitPriorityAsync("alice", home.Id, request with { Priority = "invalid" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitPriorityAsync("alice", home.Id, request with { EntityId = "switch.span_panel_other_breaker" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitPriorityAsync("alice", home.Id, request with { EntityId = "switch.dryer" }));
        clock.Advance(21);
        Assert.Null((await service.HomesAsync("alice"))[0].Devices.Single().CircuitPriority!.Priority);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitPriorityAsync("alice", home.Id, request));
        PriorityState(null);
        await service.PollAsync(home.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitPriorityAsync("alice", home.Id, request));
        CircuitState("on");
        await service.PollAsync(home.Id);
        Assert.Null((await service.HomesAsync("alice"))[0].Devices.Single().CircuitPriority);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitPriorityAsync("alice", home.Id, request));
        PriorityState("off_grid");
        await service.PollAsync(home.Id);
        await service.RevokeAsync("alice", home.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitPriorityAsync("alice", home.Id, request));
        Assert.Empty(provider.PrioritySends);
    }

    [Fact]
    public async Task RemovedPriorityOptionCancelsQueuedChange()
    {
        PriorityState("off_grid");
        var home = await Connect();
        await service.CircuitPriorityAsync("alice", home.Id, new(CircuitId, "never", "priority"));
        provider.Snapshot = provider.Snapshot! with { CircuitPriorities = [new(PriorityId, "off_grid", ["off_grid"])] };
        await service.PollAsync(home.Id);
        Assert.Equal("Cancelled", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
        Assert.Empty(provider.PrioritySends);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CircuitPriorityAsync("alice", home.Id, new(CircuitId, "never", "unsupported")));
    }

    [Fact]
    public async Task PriorityFailuresRetryAndExpireWithoutTogglingPower()
    {
        PriorityState("off_grid");
        var home = await Connect();
        await service.CircuitPriorityAsync("alice", home.Id, new(CircuitId, "never", "priority"));
        provider.Fail = true;
        await service.PollAsync(home.Id);
        Assert.Equal("Retrying", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
        provider.Fail = false;
        clock.Advance(6);
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.PrioritySends.Count);
        Assert.Equal("AwaitingConfirmation", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
        clock.Advance(121);
        await service.PollAsync(home.Id);
        Assert.Equal("Expired", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
        Assert.Equal(2, provider.PrioritySends.Count);
        Assert.Equal(0, provider.Sends + provider.TurnOns);
    }

    [Fact]
    public async Task PrioritySchemaUpgradeIsRepeatable()
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN CircuitPrioritiesJson");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN PriorityControlEntityId");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN CircuitPriority");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        PriorityState("off_grid");
        var home = await Connect();
        await service.CircuitPriorityAsync("alice", home.Id, new(CircuitId, "never", "priority"));
        db.ChangeTracker.Clear();
        var command = await db.Set<DeviceCommand>().SingleAsync();
        Assert.Equal(PriorityId, command.PriorityControlEntityId);
        Assert.Equal("never", command.CircuitPriority);
    }
}
