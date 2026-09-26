using BaseLayer.Application.Services;
using BaseLayer.Domain.Entities;
using Xunit;

namespace BaseLayer.Tests;

public sealed class UsageLimitReachedTests
{
    private readonly UsageLimitReachedService service = new();
    private static Device Load(string id, double? watts) => new()
    {
        EntityId = id, PowerWatts = watts, State = "on", Present = true, Allowed = true
    };

    [Fact]
    public void SelectsFewestDevicesRatherThanSeveralSmallerLoads()
    {
        var actions = service.RecommendActions(15000, 11000,
            [Load("switch.small", 1500), Load("switch.medium", 3000), Load("switch.large", 5000)]);
        Assert.Equal("switch.large", Assert.Single(actions).EntityId);
        Assert.Equal(10000, 15000 - actions.Sum(a => a.PowerWatts));
    }

    [Fact]
    public void ContinuesWhenFirstShutoffOnlyReachesTheLimit()
    {
        var actions = service.RecommendActions(16000, 11000,
            [Load("switch.small", 1000), Load("switch.large", 5000), Load("switch.medium", 2000)]);
        Assert.Equal(new[] { "switch.large", "switch.medium" }, actions.Select(a => a.EntityId));
        Assert.Equal(9000, 16000 - actions.Sum(a => a.PowerWatts));
    }

    [Theory]
    [InlineData(10999, 0)]
    [InlineData(11000, 1)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void OnlyKnownUsageAtOrAboveLimitNeedsActions(double current, int count)
    {
        Assert.Equal(count, service.RecommendActions(current, 11000, [Load("switch.load", 1000)]).Count);
    }

    [Fact]
    public void ExcludesIneligibleDevicesAndInvalidPower()
    {
        var disallowed = Load("switch.disallowed", 9000); disallowed.Allowed = false;
        var missing = Load("switch.missing", 9000); missing.Present = false;
        var off = Load("switch.off", 9000); off.State = "off";
        var unknown = Load("switch.unknown", 9000); unknown.State = "unknown";
        var unavailable = Load("switch.unavailable", 9000); unavailable.State = "unavailable";
        var actions = service.RecommendActions(13000, 11000,
            [disallowed, missing, off, unknown, unavailable, Load("climate.room", 9000),
             Load("switch.no_reading", null), Load("switch.nan", double.NaN),
             Load("switch.infinite", double.PositiveInfinity), Load("switch.zero", 0),
             Load("switch.negative", -1000), Load("switch.eligible", 3000)]);
        Assert.Equal("switch.eligible", Assert.Single(actions).EntityId);
    }

    [Fact]
    public void InsufficientAvailablePowerReturnsAllEligibleDevicesWithoutChangingThem()
    {
        Device[] devices = [Load("switch.b", 1000), Load("switch.a", 1000)];
        var actions = service.RecommendActions(15000, 11000, devices);
        Assert.Equal(new[] { "switch.a", "switch.b" }, actions.Select(a => a.EntityId));
        Assert.Equal(13000, 15000 - actions.Sum(a => a.PowerWatts));
        Assert.All(devices, d => Assert.Equal("on", d.State));
        Assert.Empty(service.RecommendActions(15000, 11000, []));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidLimits(double limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => service.RecommendActions(13000, limit, []));
    }
}
