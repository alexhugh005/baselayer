using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;
public sealed partial class PlatformTests
{
    private static ProviderSnapshot LimitSnapshot(double? target = 100, double amps = 32) =>
        EvSnapshot(4000, amps, 3, "off") with { EvBatterySensors = [new("sensor.ev_battery", "EV battery", 50, 75,
            new("input_number.ev_target", "EV charge target", target, 50, 100, 1))] };

    [Fact]
    public async Task EvLimitAndCurrentConfirmInOrderWithoutStartingTheCharger()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot();
        await service.PollAsync(home.Id);
        var command = await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "limit", 80));
        Assert.Equal(80, command.ChargeLimitPercent);
        await service.PollAsync(home.Id);
        Assert.Equal(("input_number.ev_target", 80d), Assert.Single(provider.ChargeLimitSends));
        Assert.Empty(provider.CurrentSends);
        await service.PollAsync(home.Id);
        Assert.Single(provider.ChargeLimitSends);
        Assert.Empty(provider.CurrentSends);
        provider.Snapshot = LimitSnapshot(80);
        await service.PollAsync(home.Id);
        Assert.Equal(16, Assert.Single(provider.CurrentSends).Amps);
        provider.Snapshot = LimitSnapshot(80, 16);
        await service.PollAsync(home.Id);
        home = (await service.HomesAsync("alice"))[0];
        Assert.Equal("Confirmed", Assert.Single(home.Commands).Status);
        Assert.Equal("off", Assert.Single(home.Devices).State);
        Assert.Equal(0, provider.TurnOns);
        Assert.Equal(command.Id, (await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "limit", 80))).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "limit", 90)));
    }

    [Fact]
    public async Task EvLimitValidatesRangeStepAndAvailability()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot();
        await service.PollAsync(home.Id);
        foreach (var target in new[] { 49d, 101, 80.5, double.NaN })
            await Assert.ThrowsAsync<ArgumentException>(() => service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "invalid", target)));
        provider.Snapshot = LimitSnapshot(null);
        await service.PollAsync(home.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "unavailable", 80)));
        Assert.Empty(provider.ChargeLimitSends);
        Assert.Empty(provider.CurrentSends);
    }

    [Fact]
    public async Task EvLimitFailureDoesNotApplyCurrentOrClaimConfirmation()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot();
        await service.PollAsync(home.Id);
        await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "fail", 80));
        provider.Fail = true;
        await service.PollAsync(home.Id);
        Assert.Empty(provider.CurrentSends);
        Assert.Equal("Retrying", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
        clock.Advance(61);
        provider.Fail = false;
        await service.PollAsync(home.Id);
        Assert.Equal("Expired", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
        Assert.Empty(provider.CurrentSends);
    }

    [Fact]
    public async Task EvLimitMappingLossCancelsPendingPlan()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot();
        await service.PollAsync(home.Id);
        await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "mapping", 80));
        provider.Snapshot = EvSnapshot();
        await service.PollAsync(home.Id);
        Assert.Equal("Cancelled", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
        Assert.Empty(provider.ChargeLimitSends);
        Assert.Empty(provider.CurrentSends);
    }

    [Fact]
    public async Task EvLimitSchemaUpgradePreservesExistingCommands()
    {
        var home = await EvHome("Sometimes", false);
        await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "old-command"));
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN ChargeLimitPercent");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand DROP COLUMN ChargeLimitControlEntityId");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        var command = Assert.Single((await service.HomesAsync("alice"))[0].Commands);
        Assert.Equal(16, command.CurrentAmps);
        Assert.Null(command.ChargeLimitPercent);
    }
}
