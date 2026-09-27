using BaseLayer.Application.Services;
using BaseLayer.Data;
using BaseLayer.Data.Repositories;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    [Fact]
    public async Task DefaultDetectorAllowsOverLimitUsageWithoutActionsOrRecommendations()
    {
        var home = await SmartHome(23000);
        var platform = new PlatformService(new PlatformRepository(db, new DatabaseGate()), provider,
            new PlainProtector(), clock, new HomeOperationGate(), new UsageLimitReachedService(), new GridOutageDetectionService());
        await platform.PollAsync(home.Id);
        var result = Assert.Single(await platform.HomesAsync("alice"));
        Assert.Equal("low", result.GridOutageRisk);
        Assert.Equal(23000, result.ProjectedWatts);
        Assert.Equal("monitoring", result.SmartPowerOffStatus);
        Assert.Empty(result.Commands);
        Assert.DoesNotContain(result.Devices, d => d.Recommended);
        Assert.Equal(0, provider.Sends);
    }

    [Theory]
    [InlineData("low", 0)]
    [InlineData("medium", 1)]
    [InlineData("high", 1)]
    public async Task EvReductionRequiresElevatedRisk(string risk, int sends)
    {
        gridRisk.Risk = risk;
        var home = await EvHome();
        await service.PollAsync(home.Id);
        Assert.Equal(sends, provider.CurrentSends.Count);
        Assert.Equal(0, provider.Sends);
    }

    [Theory]
    [InlineData(15000, 1, 0)]
    [InlineData(23000, 4, 1)]
    [InlineData(26000, 5, 2)]
    [InlineData(30000, 5, 2)]
    public async Task HighRiskUsesAnytimeThenOnlyNecessarySometimesAndQueuesEveryShutoff(double total, int count, int sometimes)
    {
        gridRisk.Risk = GridOutageRisk.High;
        var home = await SmartHome(total);
        await service.PollAsync(home.Id);
        var result = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal(count, provider.Sends);
        Assert.Equal(count, result.RestoreQueue!.Count);
        Assert.Equal(sometimes, result.Commands.Count(c => result.Devices.Single(d => d.EntityId == c.EntityId).ShutoffLevel == "Sometimes"));
        Assert.DoesNotContain(result.Commands, c => c.EntityId == "switch.never");
        Assert.All(result.Commands, c => Assert.True(c.Automatic));
        db.ChangeTracker.Clear();
        Assert.Equal(count, Assert.Single(await service.HomesAsync("alice")).RestoreQueue!.Count);
    }

    [Fact]
    public async Task EscalationFromMediumToHighAutomaticallyHandlesPreviouslyRecommendedDevices()
    {
        var home = await SmartHome(26000);
        await service.PollAsync(home.Id);
        provider.Snapshot = SmartSnapshot(17000, "switch.large", "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        var medium = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal("review", medium.SmartPowerOffStatus);
        Assert.Equal(2, medium.Devices.Count(d => d.Recommended));
        Assert.Equal(3, provider.Sends);
        gridRisk.Risk = GridOutageRisk.High;
        await service.PollAsync(home.Id);
        Assert.Equal(5, provider.Sends);
        provider.Snapshot = SmartSnapshot(9000, "switch.large", "switch.medium", "switch.small", "switch.sometimes", "switch.extra");
        await service.PollAsync(home.Id);
        var high = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal("monitoring", high.SmartPowerOffStatus);
        Assert.Equal(5, high.RestoreQueue!.Count);
        Assert.All(high.Commands, c => Assert.Equal("Confirmed", c.Status));
    }

    [Fact]
    public async Task ReturningLowCancelsRetriesButConfirmsSentShutoffsAndRestoresAboveLimit()
    {
        gridRisk.Risk = GridOutageRisk.High;
        var home = await SmartHome(23000);
        await service.PollAsync(home.Id);
        gridRisk.Risk = GridOutageRisk.Low;
        // Only the Sometimes device applied the request; total still exceeds 11 kW.
        provider.Snapshot = SmartSnapshot(17000, "switch.sometimes");
        await service.PollAsync(home.Id);
        var result = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal("Confirmed", result.Commands.Single(c => c.EntityId == "switch.sometimes").Status);
        Assert.All(result.Commands.Where(c => c.EntityId != "switch.sometimes"), c => Assert.Equal("Cancelled", c.Status));
        await PollFor(home.Id, 15);
        Assert.Equal(4, provider.Sends);
        Assert.Equal(1, provider.TurnOns);
        var on = Assert.Single(Assert.Single(await service.HomesAsync("alice")).Commands, c => c.Action == "On");
        Assert.Equal("switch.sometimes", on.EntityId);
        provider.Snapshot = SmartSnapshot(23000);
        await service.PollAsync(home.Id);
        Assert.Empty(Assert.Single(await service.HomesAsync("alice")).RestoreQueue!);
    }

    [Fact]
    public async Task HighToMediumStopsSometimesRetriesWhileKeepingConfirmedShutoffsQueued()
    {
        gridRisk.Risk = GridOutageRisk.High;
        var home = await SmartHome(26000);
        await service.PollAsync(home.Id);
        gridRisk.Risk = GridOutageRisk.Medium;
        provider.Snapshot = SmartSnapshot(18000, "switch.large", "switch.medium", "switch.small", "switch.sometimes");
        await service.PollAsync(home.Id);
        var result = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal("Confirmed", result.Commands.Single(c => c.EntityId == "switch.sometimes").Status);
        Assert.Equal("Cancelled", result.Commands.Single(c => c.EntityId == "switch.extra").Status);
        Assert.Contains(result.RestoreQueue!, d => d.EntityId == "switch.sometimes");
        Assert.Equal(5, provider.Sends);
    }

    [Fact]
    public async Task RenewedHighRiskReconsidersSometimesCommandsCancelledAtMedium()
    {
        gridRisk.Risk = GridOutageRisk.High;
        var home = await SmartHome(26000);
        await service.PollAsync(home.Id);
        gridRisk.Risk = GridOutageRisk.Medium;
        provider.Snapshot = SmartSnapshot(17000, "switch.large", "switch.medium", "switch.small");
        await service.PollAsync(home.Id);
        Assert.Equal(5, provider.Sends);
        gridRisk.Risk = GridOutageRisk.High;
        await service.PollAsync(home.Id);
        Assert.Equal(7, provider.Sends);
    }

    [Fact]
    public async Task ReturningLowRestoresOriginalEvCurrentEvenAboveLimit()
    {
        var home = await ReducedEv();
        gridRisk.Risk = GridOutageRisk.Low;
        provider.Snapshot = EvSnapshot(14800, 20, 4800);
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 5);
        Assert.Equal(32, provider.CurrentSends.Last().Amps);
        Assert.Equal(2, provider.CurrentSends.Count);
        provider.Snapshot = EvSnapshot(17680, 32, 7680);
        await service.PollAsync(home.Id);
        Assert.Empty(Assert.Single(await service.HomesAsync("alice")).RestoreQueue!);
        Assert.Equal(0, provider.Sends);
    }

    [Fact]
    public async Task LowRiskStopsUnconfirmedEvReductionRetries()
    {
        var home = await EvHome();
        await service.PollAsync(home.Id);
        gridRisk.Risk = GridOutageRisk.Low;
        await PollFor(home.Id, 20);
        Assert.Single(provider.CurrentSends);
        Assert.Equal("Cancelled", Assert.Single(Assert.Single(await service.HomesAsync("alice")).Commands).Status);
    }

    [Fact]
    public async Task SometimesDeviceCanRestoreWhenCapacityOpensAtHighRisk()
    {
        gridRisk.Risk = GridOutageRisk.High;
        var home = await SmartHome(10000);
        await service.PollAsync(home.Id);
        await service.TurnOffAsync("alice", home.Id, new(["switch.sometimes"], "approved"));
        await service.PollAsync(home.Id);
        provider.Snapshot = SmartSnapshot(3000, "switch.sometimes");
        await service.PollAsync(home.Id);
        await PollFor(home.Id, 15);
        Assert.Equal(1, provider.TurnOns);
    }

    [Fact]
    public async Task LegacyDatabaseGainsLowRiskWithoutLosingRestorationQueue()
    {
        var home = await QueueLoads();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN GridOutageRisk");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        var upgraded = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal("low", upgraded.GridOutageRisk);
        Assert.Equal(3, upgraded.RestoreQueue!.Count);
    }
}
