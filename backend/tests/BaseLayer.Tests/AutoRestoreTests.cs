using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    private async Task PollFor(Guid id, int seconds)
    {
        for (var elapsed = 0; elapsed < seconds; elapsed++)
        {
            clock.Advance(1);
            await service.PollAsync(id);
        }
    }
    private async Task<HomeDto> QueueLoads()
    {
        var home = await SmartHome(23000);
        await service.PollAsync(home.Id);
        provider.Snapshot = SmartSnapshot(1000, "switch.large", "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        return (await service.HomesAsync("alice"))[0];
    }
    [Fact]
    public async Task TracksConfirmedShutoffsAndPersistsPreShutoffEstimates()
    {
        var home = await QueueLoads();
        Assert.Equal(3, home.RestoreQueue!.Count);
        Assert.Equal(5000, home.RestoreQueue.Single(d => d.EntityId == "switch.large").EstimatedWatts);
        Assert.Equal(0, home.Devices.Single(d => d.EntityId == "switch.large").PowerWatts);
        db.ChangeTracker.Clear();
        var persisted = (await service.HomesAsync("alice"))[0];
        Assert.Equal(3, persisted.RestoreQueue!.Count);
        Assert.DoesNotContain(persisted.RestoreQueue, d => d.EntityId == "switch.never");
        Assert.Equal(0, provider.TurnOns);
    }
    [Fact]
    public async Task RestoresOneDeviceAtATimeWithMinimumOffTimeAndSettlingBetweenDevices()
    {
        var home = await QueueLoads();
        await PollFor(home.Id, 14);
        Assert.Equal(0, provider.TurnOns);
        await PollFor(home.Id, 1);
        Assert.Equal(1, provider.TurnOns);
        var result = (await service.HomesAsync("alice"))[0];
        Assert.Equal("switch.large", Assert.Single(result.Commands, c => c.Action == "On").EntityId);
        await PollFor(home.Id, 1); // No confirmation yet, no second device.
        Assert.Equal(1, provider.TurnOns);
        provider.Snapshot = SmartSnapshot(6000, "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        Assert.Equal(2, (await service.HomesAsync("alice"))[0].RestoreQueue!.Count);
        await PollFor(home.Id, 5);
        Assert.Equal(1, provider.TurnOns); // First stable reading occurred 1 second after confirmation.
        await PollFor(home.Id, 1);
        Assert.Equal(2, provider.TurnOns);
        Assert.Contains((await service.HomesAsync("alice"))[0].Commands, c => c.Action == "On" && c.EntityId == "switch.medium");
    }
    [Fact]
    public async Task SkipsLoadsThatDoNotFitAndReservesPowerBuffer()
    {
        var home = await QueueLoads();
        provider.Snapshot = SmartSnapshot(9500, "switch.large", "switch.medium", "switch.small");
        await PollFor(home.Id, 140);
        Assert.Equal(0, provider.TurnOns); // 9.5 + 1 + .5 reaches limit; keep strictly below.
        provider.Snapshot = SmartSnapshot(9000, "switch.large", "switch.medium", "switch.small");
        await PollFor(home.Id, 6);
        Assert.Equal(1, provider.TurnOns);
        Assert.Equal("switch.small", Assert.Single((await service.HomesAsync("alice"))[0].Commands, c => c.Action == "On").EntityId);
    }
    [Fact]
    public async Task UnknownReadingsAndPollingGapsRestartTheStableHeadroomWindow()
    {
        var home = await QueueLoads();
        await PollFor(home.Id, 4);
        provider.Snapshot = SmartSnapshot(null, "switch.large", "switch.medium", "switch.small");
        await PollFor(home.Id, 60);
        provider.Snapshot = SmartSnapshot(1000, "switch.large", "switch.medium", "switch.small");
        await PollFor(home.Id, 4);
        Assert.Equal(0, provider.TurnOns);
        clock.Advance(30); // A gap must not count as 30 seconds of observed spare capacity.
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 4);
        Assert.Equal(0, provider.TurnOns);
        await PollFor(home.Id, 1);
        Assert.Equal(1, provider.TurnOns);
    }
    [Fact]
    public async Task CapacityLossCancelsRestoreRetryAndReturnsDeviceToWaiting()
    {
        var home = await QueueLoads();
        await PollFor(home.Id, 15);
        Assert.Equal(1, provider.TurnOns);
        provider.Snapshot = SmartSnapshot(10000, "switch.large", "switch.medium", "switch.small");
        await PollFor(home.Id, 30);
        var result = (await service.HomesAsync("alice"))[0];
        Assert.Equal(1, provider.TurnOns);
        Assert.Equal("Cancelled", Assert.Single(result.Commands, c => c.Action == "On").Status);
        Assert.Equal("waiting", result.RestoreQueue!.Single(d => d.EntityId == "switch.large").Status);
    }
    [Fact]
    public async Task FailedRestoresStayVisibleWithoutUnlimitedAutomaticRetries()
    {
        var home = await QueueLoads();
        provider.Fail = true;
        await PollFor(home.Id, 15);
        Assert.Equal(1, provider.TurnOns);
        clock.Advance(121);
        await service.PollAsync(home.Id);
        Assert.Equal("failed", (await service.HomesAsync("alice"))[0].RestoreQueue!.Single(d => d.EntityId == "switch.large").Status);
        await service.KeepOffAsync("alice", home.Id, "switch.medium");
        await service.KeepOffAsync("alice", home.Id, "switch.small");
        await PollFor(home.Id, 180);
        Assert.Equal(1, provider.TurnOns);
    }
    [Fact]
    public async Task RestoreRequiresEnabledAutomationAndKeepOffIsOwnerBound()
    {
        var home = await QueueLoads();
        await service.SmartPowerOffAsync("alice", home.Id, new(false));
        await PollFor(home.Id, 180);
        Assert.Equal(0, provider.TurnOns);
        Assert.All((await service.HomesAsync("alice"))[0].RestoreQueue!, d => Assert.Equal("paused", d.Status));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.KeepOffAsync("bob", home.Id, "switch.large"));
        foreach (var item in home.RestoreQueue!) await service.KeepOffAsync("alice", home.Id, item.EntityId);
        await service.SmartPowerOffAsync("alice", home.Id, new(true));
        await PollFor(home.Id, 180);
        Assert.Equal(0, provider.TurnOns);
        Assert.Empty((await service.HomesAsync("alice"))[0].RestoreQueue!);
    }
    [Fact]
    public async Task ManualSometimesShutoffsAreQueuedButAlreadyOffDevicesAreNot()
    {
        var home = await SmartHome(10000);
        await service.PollAsync(home.Id);
        await service.TurnOffAsync("alice", home.Id, new(["switch.sometimes"], "approved-sometimes"));
        await service.PollAsync(home.Id);
        provider.Snapshot = SmartSnapshot(4000, "switch.sometimes", "switch.small");
        await service.PollAsync(home.Id);
        var entry = Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!);
        Assert.Equal("switch.sometimes", entry.EntityId);
        Assert.Equal(6000, entry.EstimatedWatts);
        await PollFor(home.Id, 15);
        Assert.Equal(1, provider.TurnOns);
    }
    [Fact]
    public async Task ExternalTurnOnRemovesQueueEntryWithoutSendingAnOnCommand()
    {
        var home = await QueueLoads();
        provider.Snapshot = SmartSnapshot(6000, "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        Assert.DoesNotContain((await service.HomesAsync("alice"))[0].RestoreQueue!, d => d.EntityId == "switch.large");
        Assert.Equal(0, provider.TurnOns);
    }
    [Fact]
    public async Task UnmeteredManualShutoffsRemainQueuedWithoutAutomaticRestoration()
    {
        var home = await SmartHome(10000);
        await service.SettingsAsync("alice", home.Id, new(false, false, ["switch.sometimes"], "sensor.total", new()));
        await service.PollAsync(home.Id);
        await service.TurnOffAsync("alice", home.Id, new(["switch.sometimes"], "unmetered"));
        await service.PollAsync(home.Id);
        provider.Snapshot = SmartSnapshot(1000, "switch.sometimes");
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 180);
        var entry = Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!);
        Assert.Null(entry.EstimatedWatts);
        Assert.Equal("unknown", entry.Status);
        Assert.Equal(0, provider.TurnOns);
    }
    [Fact]
    public async Task DeviceAlreadyOffBeforeCommandWasSentDoesNotEnterRestoreQueue()
    {
        var home = await SmartHome(10000);
        await service.PollAsync(home.Id);
        await service.TurnOffAsync("alice", home.Id, new(["switch.sometimes"], "already-off"));
        provider.Snapshot = SmartSnapshot(1000, "switch.sometimes");
        await service.PollAsync(home.Id);
        Assert.Empty((await service.HomesAsync("alice"))[0].RestoreQueue!);
        Assert.Equal(0, provider.Sends);
    }
    [Theory]
    [InlineData("keep-off")]
    [InlineData("disable")]
    [InlineData("Never")]
    [InlineData("revoke")]
    public async Task QueueControlsStopAnInFlightRestoreFromRetrying(string action)
    {
        var home = await QueueLoads();
        await PollFor(home.Id, 15);
        Assert.Equal(1, provider.TurnOns);
        if (action == "keep-off")
            foreach (var entry in home.RestoreQueue!) await service.KeepOffAsync("alice", home.Id, entry.EntityId);
        else if (action == "disable")
            await service.SmartPowerOffAsync("alice", home.Id, new(false));
        else if (action == "revoke")
            await service.RevokeAsync("alice", home.Id);
        else
            await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total", new(),
                ShutoffLevels: SmartLoads.ToDictionary(d => d.Id, _ => "Never")));
        await PollFor(home.Id, 180);
        Assert.Equal(1, provider.TurnOns);
        Assert.Equal("Cancelled", Assert.Single((await service.HomesAsync("alice"))[0].Commands, c => c.Action == "On").Status);
    }

}
