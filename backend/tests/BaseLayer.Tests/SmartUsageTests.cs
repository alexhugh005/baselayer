using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Data;
using BaseLayer.Data.Repositories;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace BaseLayer.Tests;

public sealed class SmartUsagePolicyTests
{
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private Home Home() => new()
    {
        LastSeenUtc = now.UtcDateTime, HouseholdWatts = 10000,
        Devices = [Load("large", 5000), Load("medium", 3000), Load("small", 1000),
            new Device { EntityId = "switch.never", Name = "Never", State = "on", PowerWatts = 1000, Present = true, ShutoffLevel = "Never" }]
    };
    private static Device Load(string name, double watts, string state = "on") => new()
    {
        EntityId = "switch." + name, Name = name, State = state, PowerWatts = state == "on" ? watts : 0,
        LastOnWatts = watts, Present = true, Allowed = true, ShutoffLevel = "Sometimes"
    };
    private SmartUsagePlan Plan(Home home, double? target = null, double energy = 10) =>
        SmartUsagePolicy.Plan(home, new(home.Id, 13.5, energy, energy / 13.5 * 100, now, true), now, target);

    [Fact]
    public void RangeIncludesBaselineAndFewestShutoffsUseLargestLoads()
    {
        var home = Home();
        var plan = Plan(home, 5000);
        Assert.Equal(1, plan.CurrentHours);
        Assert.Equal(1, plan.ShortestHours);
        Assert.Equal(10, plan.LongestHours);
        Assert.Equal(2, plan.ProjectedHours);
        Assert.Equal("switch.large", Assert.Single(plan.Changes).EntityId);
        Assert.Equal(3, Plan(home, 1000).Changes.Count);
        Assert.DoesNotContain(plan.Changes, d => d.EntityId == "switch.never");
    }
    [Fact]
    public void OffDevicesUseSavedRunningPowerAndAccountForStandby()
    {
        var home = Home();
        home.Devices[0].State = "off";
        home.Devices[0].PowerWatts = 50;
        home.HouseholdWatts = 5050;
        var plan = Plan(home, 10000);
        Assert.Equal(10000, plan.AllOnWatts);
        Assert.Equal(10000, plan.ProjectedWatts);
        Assert.Equal("On", Assert.Single(plan.Changes).Action);
        Assert.Equal(5000, plan.Changes[0].EstimatedWatts);
    }
    [Fact]
    public void OverLimitShortestCanBePreviewedButCannotBeConfirmed()
    {
        var home = Home();
        home.Devices.Add(Load("extra", 3000, "off"));
        var plan = Plan(home, 13000);
        Assert.Equal(13000, plan.AllOnWatts);
        Assert.False(plan.CanApply);
        Assert.Contains("11 kW", plan.BlockedReason);
        Assert.True(Plan(home, 5000).CanApply);
    }
    [Fact]
    public void PlansAtTheExactLimitCannotBeImmediatelyUndoneBySmartShutoff()
    {
        var home = Home();
        home.Devices.Add(Load("extra", 1000, "off"));
        Assert.True(Plan(home, 11000).CanApply);
        home.SmartPowerOffEnabled = true;
        home.GridOutageRisk = GridOutageRisk.Medium;
        Assert.False(Plan(home, 11000).CanApply);
        Assert.Contains("Smart Shutoff", Plan(home, 11000).BlockedReason);
    }
    [Fact]
    public void UnknownOffPowerIsExcludedAndNeverAssumedZeroWhenTurningOn()
    {
        var home = Home();
        var device = Load("unknown", 0, "off");
        device.LastOnWatts = null;
        home.Devices.Add(device);
        var plan = Plan(home, 11000);
        Assert.Contains(plan.ExcludedDevices, d => d.EntityId == device.EntityId && d.Reason.Contains("saved running"));
        Assert.DoesNotContain(plan.Changes, d => d.EntityId == device.EntityId);
    }
    [Fact]
    public void ThermostatsAndNeverDevicesAreNotSelected()
    {
        var home = Home();
        home.Devices.Add(new Device { EntityId = "climate.room", State = "on", PowerWatts = 1000, Allowed = true, Present = true, ShutoffLevel = "Sometimes" });
        Assert.Equal(3, Plan(home, 0).Changes.Count);
    }
    [Fact]
    public void EmptyBatteryAndZeroLoadAreExplicit()
    {
        var home = Home();
        home.HouseholdWatts = 9000;
        Assert.Null(Plan(home, 0).LongestHours);
        var empty = Plan(home, 0, 0);
        Assert.Equal(0, empty.LongestHours);
        Assert.False(empty.CanApply);
    }
    [Fact]
    public void StaleUnknownAndOverlappingReadingsAreRejected()
    {
        var home = Home();
        home.LastSeenUtc = now.AddSeconds(-21).UtcDateTime;
        Assert.Throws<ArgumentException>(() => Plan(home));
        home.LastSeenUtc = now.UtcDateTime;
        home.HouseholdWatts = null;
        Assert.Throws<ArgumentException>(() => Plan(home));
        home.HouseholdWatts = 100;
        Assert.Throws<ArgumentException>(() => Plan(home));
        Assert.Throws<ArgumentException>(() => Plan(Home(), double.NaN));
        Assert.Throws<ArgumentException>(() => SmartUsagePolicy.Plan(Home(), new(Guid.NewGuid(), 10, 5, 50, now.AddMinutes(-1), true), now, null));
    }
    [Fact]
    public void MinimumDeviceCountHoldsAcrossEveryPossibleSubset()
    {
        var home = Home();
        for (var target = 1000; target <= 10000; target += 100)
        {
            var plan = Plan(home, target);
            var minimum = Enumerable.Range(0, 8).Select(mask => new
            {
                Count = Enumerable.Range(0, 3).Count(i => (mask & (1 << i)) != 0),
                Saved = Enumerable.Range(0, 3).Where(i => (mask & (1 << i)) != 0).Sum(i => home.Devices[i].PowerWatts!.Value)
            }).Where(set => 10000 - set.Saved <= target).Min(set => set.Count);
            Assert.Equal(minimum, plan.Changes.Count);
        }
    }
}

public sealed partial class PlatformTests
{
    private static readonly (string Id, double Watts)[] UsageLoads = [("switch.large", 5000), ("switch.medium", 3000), ("switch.small", 1000)];
    private static ProviderSnapshot UsageSnapshot(double total, params string[] off) => new(
        UsageLoads.Select(d => new ProviderDevice(d.Id, d.Id, off.Contains(d.Id) ? "off" : "on")).ToList(),
        UsageLoads.Select(d => new PowerSensorDto("sensor." + d.Id, d.Id, "W")).Append(new("sensor.total", "Total", "W")).ToList(),
        UsageLoads.ToDictionary(d => "sensor." + d.Id, d => (double?)(off.Contains(d.Id) ? 0 : d.Watts))
            .Append(new KeyValuePair<string, double?>("sensor.total", total)).ToDictionary());
    private async Task<HomeDto> UsageHome()
    {
        provider.Snapshot = UsageSnapshot(10000);
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, new(true, false, [], "sensor.total", UsageLoads.ToDictionary(d => d.Id, d => (string?)("sensor." + d.Id))));
        await service.PollAsync(home.Id);
        return (await service.HomesAsync("alice"))[0];
    }
    private SmartPowerUsageService UsageService() => new(new PlatformRepository(db, new DatabaseGate()), new UsageBattery(clock), new HomeOperationGate(), clock);
    private sealed class UsageBattery(TimeProvider clock) : IBatteryService
    {
        public Task<BatteryStatusDto> GetCurrentAsync(string ownerId, Guid homeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BatteryStatusDto(homeId, 13.5, 10, 10 / 13.5 * 100, clock.GetUtcNow(), true));
    }
    private static ApplySmartUsageRequest Confirm(SmartUsagePlan plan, string key = "confirmation") => new(plan.TargetWatts, plan.Revision, key);

    [Fact]
    public async Task UsageConfirmIsOwnerBoundIdempotentAndRejectsChangedPlans()
    {
        var home = await UsageHome();
        var usage = UsageService();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => usage.PreviewAsync("bob", home.Id));
        var plan = await usage.PreviewAsync("alice", home.Id, 5000);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => usage.ApplyAsync("bob", home.Id, Confirm(plan)));
        provider.Snapshot = UsageSnapshot(10001);
        await service.PollAsync(home.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => usage.ApplyAsync("alice", home.Id, Confirm(plan)));
        plan = await usage.PreviewAsync("alice", home.Id, 5001);
        var command = Assert.Single(await usage.ApplyAsync("alice", home.Id, Confirm(plan)));
        Assert.Equal(command.Id, Assert.Single(await usage.ApplyAsync("alice", home.Id, Confirm(plan))).Id);
        Assert.Equal(0, provider.Sends); // Confirmation queues commands; polling performs them.
        await Assert.ThrowsAsync<ArgumentException>(() => usage.ApplyAsync("alice", home.Id, Confirm(plan) with { TargetWatts = 9000 }));
    }
    [Fact]
    public async Task UsageShutoffsPersistAndAreNotUndoneByAutomaticRestore()
    {
        var home = await UsageHome();
        await service.SmartPowerOffAsync("alice", home.Id, new(true));
        var usage = UsageService();
        var plan = await usage.PreviewAsync("alice", home.Id, 1000);
        Assert.Equal(3, (await usage.ApplyAsync("alice", home.Id, Confirm(plan))).Count);
        await service.PollAsync(home.Id);
        Assert.Equal(3, provider.Sends);
        provider.Snapshot = UsageSnapshot(1000, UsageLoads.Select(d => d.Id).ToArray());
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 240);
        Assert.Equal(0, provider.TurnOns);
        db.ChangeTracker.Clear();
        var result = (await service.HomesAsync("alice"))[0];
        Assert.All(result.RestoreQueue!, entry => Assert.Equal("held", entry.Status));
        Assert.All((await db.Homes.Include(h => h.Devices).SingleAsync()).Devices, device => Assert.True(device.SmartUsageHeld));
    }
    [Fact]
    public async Task UsageTurnOnsAreSerializedAndWorkWithAutomaticRestoreDisabled()
    {
        var home = await UsageHome();
        provider.Snapshot = UsageSnapshot(2000, "switch.large", "switch.medium");
        await service.PollAsync(home.Id);
        var usage = UsageService();
        var plan = await usage.PreviewAsync("alice", home.Id, 10000);
        Assert.Equal(2, plan.Changes.Count);
        await usage.ApplyAsync("alice", home.Id, Confirm(plan));
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        provider.Snapshot = UsageSnapshot(7000, "switch.medium");
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.TurnOns);
        provider.Snapshot = UsageSnapshot(10000);
        await service.PollAsync(home.Id);
        Assert.All((await service.HomesAsync("alice"))[0].Commands, command => Assert.Equal("Confirmed", command.Status));
    }
    [Fact]
    public async Task UsageTurnOnRechecksPowerAndPermissionsBeforeSending()
    {
        var home = await UsageHome();
        provider.Snapshot = UsageSnapshot(5000, "switch.large");
        await service.PollAsync(home.Id);
        var usage = UsageService();
        var plan = await usage.PreviewAsync("alice", home.Id, 10000);
        await usage.ApplyAsync("alice", home.Id, Confirm(plan));
        provider.Snapshot = UsageSnapshot(8000, "switch.large");
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.TurnOns);
        Assert.Equal("Cancelled", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
        provider.Snapshot = UsageSnapshot(5000, "switch.large");
        await service.PollAsync(home.Id);
        plan = await usage.PreviewAsync("alice", home.Id, 10000);
        await usage.ApplyAsync("alice", home.Id, Confirm(plan, "next"));
        (await db.Homes.Include(h => h.Devices).SingleAsync()).Devices.Single(d => d.EntityId == "switch.large").ShutoffLevel = "Never";
        await db.SaveChangesAsync();
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.TurnOns);
    }
    [Fact]
    public async Task UsageEstimateSurvivesRestartAndSchemaUpgradePreservesHomes()
    {
        var home = await UsageHome();
        provider.Snapshot = UsageSnapshot(5000, "switch.large");
        await service.PollAsync(home.Id);
        db.ChangeTracker.Clear();
        Assert.Equal(5000, (await db.Homes.Include(h => h.Devices).SingleAsync()).Devices.Single(d => d.EntityId == "switch.large").LastOnWatts);
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN LastOnWatts");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN SmartUsageHeld");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN UsageBudgetWatts");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN UsageRevision");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        Assert.Equal(home.Id, (await service.HomesAsync("alice"))[0].Id);
    }
}
