using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Services;
using BaseLayer.Domain.Entities;
using Xunit;

namespace BaseLayer.Tests;

public sealed class RestorationPlanningTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private static Device Load(string id = "switch.load", double watts = 1000) => new()
    {
        EntityId = id, Allowed = true, Present = true, State = "off", ShutoffLevel = ShutoffLevels.Anytime,
        RestoreEntry = new() { QueuedUtc = Now.AddSeconds(-30), PowerOn = true, EstimatedWatts = watts }
    };

    private static Home House(params Device[] devices) => new()
    {
        SmartPowerOffEnabled = true,
        HouseholdWatts = 1000, Devices = devices.ToList()
    };

    [Fact]
    public void PowerOnPlanningDoesNotMutateStateAndAllowsCapacityToStabilizeBeforeRestart()
    {
        var device = Load();
        device.RestoreEntry!.QueuedUtc = Now;
        var home = House(device);
        IRestorationStrategy strategy = new PowerOnRestoration();

        var plan = Assert.IsType<RestorationPlan>(strategy.Plan(home, device, Now, 11000));
        Assert.Equal("On", plan.Action);
        Assert.Equal(Now.AddSeconds(15), plan.NotBeforeUtc);
        Assert.Equal(1000, plan.EstimatedWatts);
        Assert.Equal(RestoreStatus.Waiting, device.RestoreEntry.Status);
        Assert.Null(device.RestoreEntry.EligibleSinceUtc);
        Assert.Null(home.LastRestoreUtc);
        Assert.Empty(home.Commands);

        AutoRestorePolicy.QueueNext(home, Now, 11000);
        Assert.Equal(Now, device.RestoreEntry.EligibleSinceUtc);
        AutoRestorePolicy.QueueNext(home, Now.AddSeconds(14), 11000);
        Assert.Empty(home.Commands);
        AutoRestorePolicy.QueueNext(home, Now.AddSeconds(15), 11000);
        Assert.Equal("On", Assert.Single(home.Commands).Action);
    }

    [Theory]
    [InlineData(9500, false)]
    [InlineData(9499, true)]
    public void PowerOnStrategyPreservesStrictBuffer(double total, bool fits)
    {
        var device = Load();
        var home = House(device);
        home.HouseholdWatts = total;
        Assert.Equal(fits, new PowerOnRestoration().Plan(home, device, Now, 11000) is not null);
    }

    [Fact]
    public void EvPlanProposesPartialCurrentWithoutConsumingCapacityOrChangingOriginalTarget()
    {
        var device = Load("switch.ev");
        device.State = "on";
        device.PowerWatts = 4800;
        device.EvCurrentEntityId = "number.ev";
        device.RestoreEntry!.PowerOn = false;
        device.RestoreEntry.TargetCurrentAmps = 32;
        device.RestoreEntry.LastManagedCurrentAmps = 20;
        var home = House(device);
        home.HouseholdWatts = 8800;
        home.CurrentControlsJson = JsonSerializer.Serialize(new[] { new CurrentControlDto("number.ev", "EV", 20, 6, 48, 1) });
        IRestorationStrategy strategy = new EvCurrentRestoration();

        var plan = Assert.IsType<RestorationPlan>(strategy.Plan(home, device, Now, 11000));
        Assert.Equal("SetCurrent", plan.Action);
        Assert.Equal(27, plan.CurrentAmps);
        Assert.Equal("number.ev", plan.CurrentControlEntityId);
        Assert.Equal(32, device.RestoreEntry.TargetCurrentAmps);
        Assert.Equal(20, device.RestoreEntry.LastManagedCurrentAmps);
        Assert.Equal(8800, home.HouseholdWatts);
        Assert.Equal(RestoreStatus.Waiting, device.RestoreEntry.Status);
        Assert.Null(device.RestoreEntry.EligibleSinceUtc);
        Assert.Empty(home.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconciliationStillRemovesOverridesAndRecordsFailureWhileSchedulingIsBlocked(bool pendingCommand)
    {
        var revoked = Load("switch.revoked");
        revoked.Allowed = false;
        var externalOn = Load("switch.external");
        externalOn.State = "on";
        var failed = Load("switch.failed");
        failed.RestoreEntry!.BeginRestoration();
        var waiting = Load("switch.waiting");
        waiting.RestoreEntry!.ObserveCapacity(Now.AddSeconds(-10), true);
        var home = House(revoked, externalOn, failed, waiting);
        home.Commands.Add(new() { EntityId = failed.EntityId, Action = "On", Status = "Expired", CreatedUtc = Now.AddSeconds(-10) });
        if (pendingCommand) home.Commands.Add(new() { EntityId = "switch.other", Action = "Off", Status = "AwaitingConfirmation" });
        else home.LastRestoreUtc = Now;
        var commandCount = home.Commands.Count;

        AutoRestorePolicy.QueueNext(home, Now, 11000);

        Assert.Null(revoked.RestoreEntry);
        Assert.Null(externalOn.RestoreEntry);
        Assert.Equal(RestoreStatus.Failed, failed.RestoreEntry.Status);
        Assert.Null(failed.RestoreEntry.EligibleSinceUtc);
        Assert.Equal(commandCount, home.Commands.Count);
        // Pending commands reset capacity; the settling delay alone does not.
        Assert.Equal(pendingCommand ? (DateTime?)null : Now.AddSeconds(-10), waiting.RestoreEntry.EligibleSinceUtc);
    }

    [Fact]
    public void FailedCurrentRequestAtUnchangedAmpsRetainsItsTargetAndDoesNotRestartAutomatically()
    {
        var device = Load("switch.ev");
        device.State = "on";
        device.EvCurrentEntityId = "number.ev";
        device.PowerWatts = 4800;
        var entry = device.RestoreEntry!;
        entry.PowerOn = false;
        entry.TargetCurrentAmps = 32;
        entry.LastManagedCurrentAmps = 27;
        entry.BeginRestoration();
        var home = House(device);
        home.HouseholdWatts = 8800;
        home.CurrentControlsJson = JsonSerializer.Serialize(new[] { new CurrentControlDto("number.ev", "EV", 20, 6, 48, 1) });
        home.Commands.Add(new()
        {
            EntityId = device.EntityId, Action = "SetCurrent", Automatic = true, IsRestoration = true,
            Attempts = 1, PreviousCurrentAmps = 20, CurrentAmps = 27, Status = "Failed", CreatedUtc = Now
        });

        AutoRestorePolicy.QueueNext(home, Now, 11000);
        AutoRestorePolicy.QueueNext(home, Now.AddMinutes(1), 11000);

        Assert.Same(entry, device.RestoreEntry);
        Assert.Equal(32, entry.TargetCurrentAmps);
        Assert.Equal(RestoreStatus.Failed, entry.Status);
        Assert.Null(entry.EligibleSinceUtc);
        Assert.Single(home.Commands);
    }

    [Fact]
    public void FailedEntryCannotBecomeEligibleOrStartAgainWithoutExplicitResume()
    {
        var entry = new RestoreQueueEntry();
        entry.ObserveCapacity(Now, true);
        entry.BeginRestoration();
        entry.FailRestoration();
        entry.ObserveCapacity(Now.AddSeconds(10), true);
        Assert.Equal(RestoreStatus.Failed, entry.Status);
        Assert.Null(entry.EligibleSinceUtc);
        Assert.Throws<InvalidOperationException>(() => entry.BeginRestoration());

        entry.ResumeWaiting();
        entry.ObserveCapacity(Now.AddSeconds(20), true);
        Assert.Equal(RestoreStatus.Waiting, entry.Status);
        Assert.Equal(Now.AddSeconds(20), entry.EligibleSinceUtc);
    }
}
