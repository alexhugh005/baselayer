using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    [Fact]
    public async Task EvStartWaitsForLimitThenCurrentThenObservedPowerOn()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot();
        await service.PollAsync(home.Id);
        var request = new BaseLayer.Application.Contracts.EvCurrentRequest("switch.ev", 16, "start", 80, StartCharge: true);
        await service.EvCurrentAsync("alice", home.Id, request);
        await service.PollAsync(home.Id);
        Assert.Single(provider.ChargeLimitSends);
        Assert.Empty(provider.CurrentSends);
        Assert.Equal(0, provider.TurnOns);
        provider.Snapshot = LimitSnapshot(80);
        await service.PollAsync(home.Id);
        Assert.Single(provider.CurrentSends);
        Assert.Equal(0, provider.TurnOns);
        provider.Snapshot = LimitSnapshot(80, 16);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        Assert.Equal("AwaitingConfirmation", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        provider.Snapshot = EvSnapshot(4000, 16, 3840, "on") with { EvBatterySensors = LimitSnapshot(80).EvBatterySensors };
        await service.PollAsync(home.Id);
        var command = Assert.Single((await service.HomesAsync("alice"))[0].Commands);
        Assert.True(command.StartCharge);
        Assert.Equal("Confirmed", command.Status);
        Assert.Equal(command.Id, (await service.EvCurrentAsync("alice", home.Id, request)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.EvCurrentAsync("alice", home.Id, request with { StartCharge = false }));
    }

    [Fact]
    public async Task EvStartHonorsRetryLeaseWhenCurrentAlreadyMatches()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot(80, 16);
        await service.PollAsync(home.Id);
        await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "start", 80, StartCharge: true));
        await service.PollAsync(home.Id);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        clock.Advance(11);
        await service.PollAsync(home.Id);
        Assert.Equal(1, provider.TurnOns);
        clock.Advance(5);
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.TurnOns);
        clock.Advance(90);
        await service.PollAsync(home.Id);
        Assert.Equal(2, provider.TurnOns);
        Assert.Equal("Expired", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
    }

    [Fact]
    public async Task EvStartCannotRunAfterConfigurationFailureOrAccessRemoval()
    {
        var home = await EvHome("Sometimes", false);
        provider.Snapshot = LimitSnapshot();
        await service.PollAsync(home.Id);
        await service.EvCurrentAsync("alice", home.Id, new("switch.ev", 16, "start", 80, StartCharge: true));
        provider.Fail = true;
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.TurnOns);
        provider.Fail = false;
        var entity = home.Devices.Single().EntityId;
        await service.SettingsAsync("alice", home.Id, new(false, false, [], null, new(), ShutoffLevels: new() { [entity] = "Never" }));
        provider.Snapshot = LimitSnapshot(80, 16);
        await service.PollAsync(home.Id);
        Assert.Equal(0, provider.TurnOns);
        Assert.Equal("Cancelled", Assert.Single((await service.HomesAsync("alice"))[0].Commands).Status);
    }
}
