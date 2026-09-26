using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    private static readonly (string Id, double Watts, string Level)[] SmartLoads = [
        ("switch.large", 5000, "Anytime"), ("switch.medium", 3000, "Anytime"),
        ("switch.small", 1000, "Anytime"), ("switch.sometimes", 6000, "Sometimes"),
        ("switch.extra", 2000, "Sometimes"), ("switch.never", 9000, "Never")
    ];
    private static ProviderSnapshot SmartSnapshot(double? total, params string[] off) => new(
        SmartLoads.Select(d => new ProviderDevice(d.Id, d.Id, off.Contains(d.Id) ? "off" : "on")).ToList(),
        SmartLoads.Select(d => new PowerSensorDto("sensor." + d.Id, d.Id, "W")).Append(new("sensor.total", "Total", "W")).ToList(),
        SmartLoads.ToDictionary(d => "sensor." + d.Id, d => (double?)(off.Contains(d.Id) ? 0 : d.Watts))
            .Append(new KeyValuePair<string, double?>("sensor.total", total)).ToDictionary());
    private async Task<HomeDto> SmartHome(double total = 15000, bool enabled = true)
    {
        provider.Snapshot = SmartSnapshot(total);
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total",
            SmartLoads.ToDictionary(d => d.Id, d => (string?)("sensor." + d.Id)),
            ShutoffLevels: SmartLoads.ToDictionary(d => d.Id, d => d.Level)));
        await service.SmartPowerOffAsync("alice", home.Id, new(enabled));
        return home;
    }
    [Fact]
    public async Task ExistingDatabasesUpgradeWithoutEnablingAutomaticControl()
    {
        var home = await SmartHome(enabled: false);
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN LastRestoreUtc");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN RestoreWatts");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN RestoreQueuedUtc");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN RestoreEligibleSinceUtc");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN RestoreStatus");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN Action");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN EstimatedWatts");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN SmartPowerOffEnabled");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN SmartPowerOffEventId");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN ShutoffLevel");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN Automatic");
        await BaseLayer.Data.DatabaseInitializer.InitializeAsync(db);
        await BaseLayer.Data.DatabaseInitializer.InitializeAsync(db);
        var upgraded = (await service.HomesAsync("alice"))[0];
        Assert.Equal(home.Id, upgraded.Id);
        Assert.False(upgraded.SmartPowerOffEnabled);
        Assert.Equal("sensor.total", upgraded.HouseholdPowerSensorId);
        Assert.All(upgraded.Devices.Where(d => d.Allowed), d => Assert.Equal("Sometimes", d.ShutoffLevel));
        Assert.Equal("Never", upgraded.Devices.Single(d => d.EntityId == "switch.never").ShutoffLevel);
    }
    [Fact]
    public async Task SmartPowerOffUsesFewestAnytimeDevicesAndWaitsForObservedConfirmation()
    {
        var home = await SmartHome();
        await service.PollAsync(home.Id);
        var reducing = (await service.HomesAsync("alice"))[0];
        var command = Assert.Single(reducing.Commands);
        Assert.Equal("switch.large", command.EntityId);
        Assert.True(command.Automatic);
        Assert.Equal("AwaitingConfirmation", command.Status);
        Assert.Equal("reducing", reducing.SmartPowerOffStatus);
        Assert.DoesNotContain(reducing.Devices, d => d.Recommended);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        provider.Snapshot = SmartSnapshot(10000, "switch.large");
        await service.PollAsync(home.Id);
        var confirmed = (await service.HomesAsync("alice"))[0];
        Assert.Equal("Confirmed", Assert.Single(confirmed.Commands).Status);
        Assert.Equal("monitoring", confirmed.SmartPowerOffStatus);
        Assert.Equal(1, provider.Sends);
    }
    [Theory]
    [InlineData(16000, "review", 1)]
    [InlineData(17000, "review", 2)]
    [InlineData(21000, "insufficient", 2)]
    public async Task SometimesRecommendationsUseMeasuredUsageAfterAnytimeShutoffs(double after, string status, int count)
    {
        var home = await SmartHome(23000);
        await service.PollAsync(home.Id);
        Assert.Equal(3, provider.Sends);
        Assert.DoesNotContain((await service.HomesAsync("alice"))[0].Devices, d => d.Recommended);
        provider.Snapshot = SmartSnapshot(after, "switch.large", "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        var result = (await service.HomesAsync("alice"))[0];
        Assert.Equal(status, result.SmartPowerOffStatus);
        var recommended = result.Devices.Where(d => d.Recommended).ToList();
        Assert.Equal(count, recommended.Count);
        Assert.All(recommended, d => Assert.Equal("Sometimes", d.ShutoffLevel));
        Assert.Equal(3, provider.Sends);
        await service.PollAsync(home.Id);
        Assert.Equal(3, provider.Sends);
    }
    [Fact]
    public async Task SmartPowerOffDefaultsOffAndSettingsAreOwnerBoundAndPersistent()
    {
        var home = await SmartHome(enabled: false);
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.Sends);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SmartPowerOffAsync("bob", home.Id, new(true)));
        await service.SmartPowerOffAsync("alice", home.Id, new(true));
        db.ChangeTracker.Clear();
        var saved = (await service.HomesAsync("alice"))[0];
        Assert.True(saved.SmartPowerOffEnabled);
        Assert.Equal("Anytime", saved.Devices.Single(d => d.EntityId == "switch.large").ShutoffLevel);
        Assert.Equal("Never", saved.Devices.Single(d => d.EntityId == "switch.never").ShutoffLevel);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
    }
    [Fact]
    public async Task UnknownReadingsFailedPollsAndRevokedHomesCannotTriggerAutomaticShutoffs()
    {
        var home = await SmartHome();
        provider.Snapshot = SmartSnapshot(null);
        await service.PollAsync(home.Id);
        Assert.Equal("unknown", (await service.HomesAsync("alice"))[0].SmartPowerOffStatus);
        provider.Snapshot = SmartSnapshot(15000);
        provider.FailRead = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.PollAsync(home.Id));
        provider.FailRead = false;
        await service.RevokeAsync("alice", home.Id);
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.Sends);
    }
    [Theory]
    [InlineData("disable")]
    [InlineData("Never")]
    [InlineData("Sometimes")]
    [InlineData("cancel")]
    public async Task RemovingAutomaticPermissionOrCancellingStopsRetries(string change)
    {
        var home = await SmartHome();
        await service.PollAsync(home.Id);
        var command = (await service.HomesAsync("alice"))[0].Commands.Single();
        if (change == "disable")
            await service.SmartPowerOffAsync("alice", home.Id, new(false));
        else if (change == "cancel")
            await service.CancelCommandAsync("alice", home.Id, command.Id);
        else
            await service.SettingsAsync("alice", home.Id, new(false, false, [], "sensor.total",
                SmartLoads.ToDictionary(d => d.Id, d => (string?)("sensor." + d.Id)),
                ShutoffLevels: SmartLoads.ToDictionary(d => d.Id, d => d.Id == "switch.large" ? change : "Never")));
        // Other loads become unavailable, so no unrelated automatic shutoff is needed.
        provider.Snapshot = SmartSnapshot(13000, "switch.medium", "switch.small");
        clock.Advance(20);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        Assert.Equal("Cancelled", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
    }
    [Fact]
    public async Task FailedCommandsAreNotRecreatedUntilUsageRecovers()
    {
        var home = await SmartHome();
        provider.Fail = true;
        await service.PollAsync(home.Id);
        clock.Advance(121);
        // Keep only the attempted Anytime device available.
        provider.Snapshot = SmartSnapshot(15000, "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        await service.PollAsync(home.Id);
        Assert.Single((await service.HomesAsync("alice"))[0].Commands);
        Assert.Equal(1, provider.Sends);
        provider.Snapshot = SmartSnapshot(10000, "switch.large", "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        provider.Snapshot = SmartSnapshot(15000);
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.Sends);
    }
    [Fact]
    public async Task NeverDevicesCannotBeManuallyShutOffAndInvalidLevelsAreRejected()
    {
        var home = await SmartHome();
        await service.PollAsync(home.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.TurnOffAsync("alice", home.Id, new(["switch.never"], "never")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SettingsAsync("alice", home.Id,
            new(false, false, [], "sensor.total", new(), ShutoffLevels: new() { ["switch.large"] = "Invalid" })));
    }
    [Fact]
    public async Task RecoveryStopsAutomaticRetriesWithoutTurningOffMoreDevices()
    {
        var home = await SmartHome();
        await service.PollAsync(home.Id);
        provider.Snapshot = SmartSnapshot(10000); // Other household usage fell; device is still on.
        clock.Advance(20);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.Sends);
        Assert.Equal("Cancelled", (await service.HomesAsync("alice"))[0].Commands.Single().Status);
    }
}
