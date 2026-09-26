using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public static class AutoRestorePolicy
{
    // DEMO ONLY: shortened from 2 minutes off and 1 minute of stable capacity / settling.
    // Production device-specific restart delays must be restored before real deployment.
    public static readonly TimeSpan MinimumOffTime = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan StableCapacityTime = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BetweenRestorations = TimeSpan.FromSeconds(5);

    public static IOrderedEnumerable<Device> Ordered(IEnumerable<Device> devices) =>
        devices.OrderByDescending(d => d.RestoreEntry!.AtFront).ThenBy(d => d.RestoreEntry!.QueuedUtc).ThenBy(d => d.EntityId, StringComparer.Ordinal);

    public static RestoreQueueEntry Enqueue(Device device, DateTime now) =>
        device.RestoreEntry ??= new RestoreQueueEntry { DeviceId = device.Id, Device = device, QueuedUtc = now };

    public static void Clear(Device device) => device.RestoreEntry = null;

    public static void ClearCurrentRestore(Device device)
    {
        if (device.RestoreEntry is not { } entry) return;
        entry.TargetCurrentAmps = null;
        entry.LastManagedCurrentAmps = null;
        entry.AtFront = false;
        entry.ResetEligibility();
        if (!entry.PowerOn) Clear(device);
    }
    public static double? Estimate(Home home, Device device) => device.RestoreEntry is { TargetCurrentAmps: { } target, PowerOn: false }
        ? EvChargingPolicy.Control(home, device)?.Amps is { } current ? Math.Max(0, target - current) * device.EvWattsPerAmp : null
        : device.RestoreEntry?.EstimatedWatts;

    public static void ResetEligibility(Home home)
    {
        foreach (var device in home.Devices)
            if (device.RestoreEntry is { } entry) entry.ResetEligibility();
    }
    public static bool Fits(Home home, Device device, double limitWatts) =>
        PowerOnRestoration.Fits(home, device, limitWatts);

    private static readonly IRestorationStrategy PowerOnStrategy = new PowerOnRestoration();
    private static readonly IRestorationStrategy CurrentStrategy = new EvCurrentRestoration();

    public static void QueueNext(Home home, DateTime now, double limitWatts)
    {
        RestoreQueueReconciler.Reconcile(home);
        var pending = home.Commands.Any(c => !CommandPolicy.Terminal(c));
        var plans = UpdateEligibilityWindows(home, now, limitWatts, pending);
        if (pending || home.LastRestoreUtc > now - BetweenRestorations) return;

        var next = SelectNextPlan(plans, now);
        if (next is not null) QueueRestoration(home, next, now);
    }

    private static List<RestorationPlan> UpdateEligibilityWindows(Home home, DateTime now, double limitWatts, bool pending)
    {
        var plans = new List<RestorationPlan>();
        foreach (var device in home.Devices)
        {
            if (device.RestoreEntry is not { } entry) continue;
            var strategy = entry.PowerOn ? PowerOnStrategy : CurrentStrategy;
            var plan = entry.Status == RestoreStatus.Waiting ? strategy.Plan(home, device, now, limitWatts) : null;
            entry.ObserveCapacity(now, !pending && plan is not null);
            if (plan is not null) plans.Add(plan);
        }
        return plans;
    }

    private static RestorationPlan? SelectNextPlan(IEnumerable<RestorationPlan> plans, DateTime now) =>
        plans.Where(p => p.NotBeforeUtc <= now && p.Device.RestoreEntry!.EligibleSinceUtc <= now - StableCapacityTime)
            .OrderByDescending(p => p.Device.RestoreEntry!.AtFront)
            .ThenBy(p => p.Device.RestoreEntry!.QueuedUtc)
            .ThenBy(p => p.Device.EntityId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static void QueueRestoration(Home home, RestorationPlan plan, DateTime now)
    {
        plan.Device.RestoreEntry!.BeginRestoration();
        home.Commands.Add(new DeviceCommand
        {
            HomeId = home.Id, EntityId = plan.Device.EntityId, RequestedBy = home.OwnerId,
            Action = plan.Action, Automatic = true, IsRestoration = true,
            CurrentControlEntityId = plan.CurrentControlEntityId, CurrentAmps = plan.CurrentAmps,
            EstimatedWatts = plan.EstimatedWatts,
            IdempotencyKey = $"restore:{Guid.NewGuid():N}", CreatedUtc = now,
            ExpiresUtc = now + plan.Timeout, NextAttemptUtc = now, Message = plan.Message
        });
        home.LastRestoreUtc = now;
        ResetEligibility(home);
    }
    public static void Confirm(Home home, Device device, DeviceCommand command, DateTime now)
    {
        command.Status = "Confirmed";
        command.Message = $"Home Assistant state confirms the device is {(command.Action == "On" ? "on" : "off")}.";
        if (command.Action == "On")
        {
            if (device.RestoreEntry is { } entry)
            {
                entry.PowerOn = false;
                if (entry.TargetCurrentAmps is null) Clear(device);
                else
                {
                    entry.ResumeWaiting();
                    entry.AtFront = true;
                }
            }
            home.LastRestoreUtc = now;
        }
        else
        {
            var entry = Enqueue(device, now);
            entry.PowerOn = true;
            entry.EstimatedWatts = command.EstimatedWatts;
            entry.QueuedUtc = now;
            entry.ResumeWaiting();
        }
    }
}
