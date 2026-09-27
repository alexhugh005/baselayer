using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;
using BaseLayer.Application.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    private async Task<HomeDto> ReducedEv()
    {
        var home = await EvHome();
        await service.PollAsync(home.Id); // 32 -> 20 A with 6 kW other load.
        provider.Snapshot = EvSnapshot(10800, 20, 4800);
        await service.PollAsync(home.Id); // Confirm reduction; preserve 32 A original.
        return (await service.HomesAsync("alice"))[0];
    }
    [Fact]
    public async Task EvRestoresMaximumPartialCurrentThenOriginalAndOnlyThenLeavesQueue()
    {
        var home = await ReducedEv();
        Assert.Equal(32, Assert.Single(home.RestoreQueue!).TargetAmps);
        db.ChangeTracker.Clear();
        Assert.Equal(32, Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!).TargetAmps);
        // 4 kW other load: 10.5 kW capacity allows 27 A, not the original 32 A yet.
        provider.Snapshot = EvSnapshot(8800, 20, 4800);
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 4);
        Assert.Single(provider.CurrentSends);
        await PollFor(home.Id, 1);
        Assert.Equal(27, provider.CurrentSends.Last().Amps);
        Assert.Equal("restoring", Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!).Status);
        provider.Snapshot = EvSnapshot(10480, 27, 6480);
        await service.PollAsync(home.Id);
        var partial = Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!);
        Assert.Equal(27, partial.CurrentAmps);
        Assert.Equal(32, partial.TargetAmps);
        Assert.Equal("waiting", partial.Status);
        // Free another 2 kW. One fresh poll plus 5 stable seconds allows full restoration.
        provider.Snapshot = EvSnapshot(8480, 27, 6480);
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 5);
        Assert.Equal(32, provider.CurrentSends.Last().Amps);
        Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!); // Acknowledgment alone isn't completion.
        provider.Snapshot = EvSnapshot(9680, 32, 7680);
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Empty(home.RestoreQueue!);
        Assert.Equal(0, provider.Sends);
        var restore = home.Commands.First(c => c.IsRestoration);
        Assert.Equal(27, restore.PreviousCurrentAmps);
        Assert.Equal(32, restore.CurrentAmps);
        Assert.Equal("Confirmed", restore.Status);
    }
    [Fact]
    public async Task FurtherReductionsPreserveFirstOriginalCurrent()
    {
        var home = await ReducedEv();
        provider.Snapshot = EvSnapshot(11800, 20, 4800);
        await service.PollAsync(home.Id);
        Assert.Equal(16, provider.CurrentSends.Last().Amps);
        provider.Snapshot = EvSnapshot(10840, 16, 3840);
        await service.PollAsync(home.Id);
        Assert.Equal(32, Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!).TargetAmps);
    }
    [Theory]
    [InlineData("external")]
    [InlineData("manual")]
    [InlineData("keep-rate")]
    [InlineData("external-off")]
    public async Task ManualOverridesRemoveEvCompensation(string action)
    {
        var home = await ReducedEv();
        if (action == "external") provider.Snapshot = EvSnapshot(9840, 16, 3840);
        if (action == "external-off") provider.Snapshot = EvSnapshot(6000, 20, 0, "off");
        if (action == "manual") await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "manual-override"));
        if (action == "keep-rate") await service.KeepOffAsync("alice", home.Id, "switch.ev");
        await service.PollAsync(home.Id);
        Assert.Empty((await service.HomesAsync("alice"))[0].RestoreQueue!);
        Assert.DoesNotContain((await service.HomesAsync("alice"))[0].Commands, c => c.IsRestoration);
    }
    [Fact]
    public async Task LostCapacityCancelsIncreaseWithoutLosingOriginalTarget()
    {
        var home = await ReducedEv();
        provider.Snapshot = EvSnapshot(8800, 20, 4800);
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 5);
        Assert.Equal(27, provider.CurrentSends.Last().Amps);
        // HA has not applied it, and headroom disappears before a retry.
        provider.Snapshot = EvSnapshot(10800, 20, 4800);
        await PollFor(home.Id, 5);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal("Cancelled", home.Commands.First(c => c.IsRestoration).Status);
        Assert.Equal(32, Assert.Single(home.RestoreQueue!).TargetAmps);
        Assert.Equal(2, provider.CurrentSends.Count);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("offline")]
    [InlineData("disabled")]
    [InlineData("Sometimes")]
    public async Task EvIncreaseWaitsForValidCapacityAndPermission(string condition)
    {
        var home = await ReducedEv();
        provider.Snapshot = EvSnapshot(8800, 20, 4800);
        if (condition == "unknown") provider.Snapshot = EvSnapshot(null, 20, 4800);
        if (condition == "offline") provider.Snapshot = EvSnapshot(8800, null, 4800);
        if (condition == "disabled") await service.SmartPowerOffAsync("alice", home.Id, new(false));
        if (condition == "Sometimes")
            await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total", new() { ["switch.ev"] = "sensor.ev" }, ShutoffLevels: new() { ["switch.ev"] = "Sometimes" }));
        await PollFor(home.Id, 20);
        Assert.Single(provider.CurrentSends);
        Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!);
    }
    [Fact]
    public async Task FailedEvIncreaseStaysVisibleWithoutRecreation()
    {
        var home = await ReducedEv();
        provider.Snapshot = EvSnapshot(8800, 20, 4800);
        provider.Fail = true;
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 5);
        clock.Advance(31);
        await service.PollAsync(home.Id);
        Assert.Equal("failed", Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!).Status);
        var sends = provider.CurrentSends.Count;
        await PollFor(home.Id, 20);
        Assert.Equal(sends, provider.CurrentSends.Count);
    }
    [Fact]
    public async Task EvStillRestoresOriginalAmpsAfterBeingFullyShutOff()
    {
        var home = await ReducedEv();
        provider.Snapshot = EvSnapshot(16000, 20, 4800); // Minimum EV draw cannot fit.
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        provider.Snapshot = EvSnapshot(1000, 20, 0, "off");
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 15);
        Assert.Equal(1, provider.TurnOns);
        provider.Snapshot = EvSnapshot(5800, 20, 4800);
        await service.PollAsync(home.Id);
        Assert.Equal(32, Assert.Single((await service.HomesAsync("alice"))[0].RestoreQueue!).TargetAmps);
        await PollFor(home.Id, 6);
        Assert.Equal(32, provider.CurrentSends.Last().Amps);
        provider.Snapshot = EvSnapshot(8680, 32, 7680);
        await service.PollAsync(home.Id);
        Assert.Empty((await service.HomesAsync("alice"))[0].RestoreQueue!);
    }
    [Fact]
    public async Task DemoRequiresBothFifteenSecondsOffAndFiveSecondsOfCapacity()
    {
        var home = await QueueLoads();
        provider.Snapshot = SmartSnapshot(10500, "switch.large", "switch.medium", "switch.small");
        await PollFor(home.Id, 15);
        Assert.Equal(0, provider.TurnOns);
        provider.Snapshot = SmartSnapshot(1000, "switch.large", "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 4);
        Assert.Equal(0, provider.TurnOns);
        await PollFor(home.Id, 1);
        Assert.Equal(1, provider.TurnOns);
    }
    [Fact]
    public void PartialEvRestoreKeepsFrontPriorityAndSerializesCapacity()
    {
        var now = DateTime.UtcNow;
        var ev = new Device { EntityId = "switch.ev", Present = true, Allowed = true, State = "on", ShutoffLevel = "Anytime",
            EvCurrentEntityId = "number.ev", EvWattsPerAmp = 240, PowerWatts = 6480,
            RestoreEntry = new() { TargetCurrentAmps = 32, LastManagedCurrentAmps = 27, QueuedUtc = now.AddSeconds(-30),
                AtFront = true, EligibleSinceUtc = now.AddSeconds(-10) } };
        var load = new Device { EntityId = "switch.other", Present = true, Allowed = true, State = "off", ShutoffLevel = "Anytime",
            RestoreEntry = new() { QueuedUtc = now.AddSeconds(-60), EstimatedWatts = 1000, PowerOn = true, EligibleSinceUtc = now.AddSeconds(-10) } };
        var home = new Home { GridOutageRisk = GridOutageRisk.Medium, SmartPowerOffEnabled = true, HouseholdWatts = 7480, Devices = [load, ev],
            CurrentControlsJson = System.Text.Json.JsonSerializer.Serialize(new[] { new CurrentControlDto("number.ev", "EV", 27, 6, 48, 1) }) };
        AutoRestorePolicy.QueueNext(home, now, 11000);
        Assert.Equal("switch.ev", Assert.Single(home.Commands).EntityId);
        Assert.Equal(32, home.Commands[0].CurrentAmps);
        AutoRestorePolicy.QueueNext(home, now.AddSeconds(10), 11000);
        Assert.Single(home.Commands); // Never allocate that same headroom to a second device.
    }
    [Fact]
    public async Task EvRestoreSchemaUpgradeKeepsLegacyPowerOnQueue()
    {
        var home = await QueueLoads();
        db.ChangeTracker.Clear();
        await RecreateLegacyRestoreColumns(includeEv: false);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN PreviousCurrentAmps");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN IsRestoration");
        await BaseLayer.Data.DatabaseInitializer.InitializeAsync(db);
        await BaseLayer.Data.DatabaseInitializer.InitializeAsync(db);
        await PollFor(home.Id, 15);
        Assert.Equal(1, provider.TurnOns);
        Assert.All((await service.HomesAsync("alice"))[0].RestoreQueue!, item => Assert.Null(item.TargetAmps));
    }

}
