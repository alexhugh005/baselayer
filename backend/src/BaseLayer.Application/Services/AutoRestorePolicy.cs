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
        devices.OrderByDescending(d => d.RestoreAtFront).ThenBy(d => d.RestoreQueuedUtc).ThenBy(d => d.EntityId, StringComparer.Ordinal);

    public static void Clear(Device device)
    {
        device.RestoreQueuedUtc = null;
        device.RestoreWatts = null;
        device.RestoreCurrentAmps = null;
        device.LastManagedCurrentAmps = null;
        device.RestoreAtFront = false;
        device.RestorePowerOn = false;
        device.RestoreEligibleSinceUtc = null;
        device.RestoreStatus = "waiting";
    }
    public static void ClearCurrentRestore(Device device)
    {
        device.RestoreCurrentAmps = null;
        device.LastManagedCurrentAmps = null;
        device.RestoreAtFront = false;
        device.RestoreEligibleSinceUtc = null;
        if (!device.RestorePowerOn) Clear(device);
    }
    public static double? Estimate(Home home, Device device) => device.RestoreCurrentAmps is { } target && !device.RestorePowerOn
        ? EvChargingPolicy.Control(home, device)?.Amps is { } current ? Math.Max(0, target - current) * device.EvWattsPerAmp : null
        : device.RestoreWatts;

    public static void ResetEligibility(Home home)
    {
        foreach (var device in home.Devices) device.RestoreEligibleSinceUtc = null;
    }
    public static bool Fits(Home home, Device device, double limitWatts) =>
        home.SmartPowerOffEnabled && !SmartPanelCircuit.IsCircuit(device.EntityId) && !device.SmartUsageHeld && device.Present && device.Allowed && device.ShutoffLevel != ShutoffLevels.Never &&
        !device.EntityId.StartsWith("climate.") && device.State == "off" && device.RestorePowerOn &&
        home.HouseholdWatts is { } current && double.IsFinite(current) && current >= 0 &&
        device.RestoreWatts is { } estimate && double.IsFinite(estimate) && estimate > 0 &&
        current + estimate + Math.Max(500, estimate * 0.1) < limitWatts;

    private static bool CanRestore(Home home, Device device, DateTime now, double limitWatts) =>
        device.RestorePowerOn
            ? device.RestoreQueuedUtc <= now - MinimumOffTime && Fits(home, device, limitWatts)
            : EvChargingPolicy.Increase(home, device, limitWatts) is not null;

    public static void QueueNext(Home home, DateTime now, double limitWatts)
    {
        var pending = home.Commands.Any(c => !CommandPolicy.Terminal(c));
        foreach (var device in home.Devices.Where(d => d.RestoreQueuedUtc != null))
        {
            var restoring = home.Commands.Where(c => c.EntityId == device.EntityId && (c.Action == "On" || c.IsRestoration)).OrderByDescending(c => c.CreatedUtc).FirstOrDefault();
            if (!device.Allowed || device.ShutoffLevel == ShutoffLevels.Never)
            {
                Clear(device);
                continue;
            }
            var devicePending = home.Commands.Where(c => c.EntityId == device.EntityId && !CommandPolicy.Terminal(c)).ToList();
            if (device.RestoreCurrentAmps is { } target && !devicePending.Any(c => c.Action == "SetCurrent") &&
                EvChargingPolicy.Control(home, device)?.Amps is { } amps)
            {
                var lastCurrent = home.Commands.Where(c => c.EntityId == device.EntityId && c.Action == "SetCurrent" && c.Automatic && c.Attempts > 0)
                    .OrderByDescending(c => c.CreatedUtc).FirstOrDefault();
                var failedUnchanged = lastCurrent is { Status: "Failed" or "Expired" or "Cancelled", PreviousCurrentAmps: { } previous } && Math.Abs(amps - previous) < 0.000001;
                if (amps >= target || (!failedUnchanged && device.LastManagedCurrentAmps is { } managed && Math.Abs(amps - managed) > 0.000001))
                    ClearCurrentRestore(device); // Target reached externally or a manual override: relinquish ownership.
            }
            if (device.RestoreQueuedUtc is null) continue;
            if (device.Present && device.State == "off" && !device.RestorePowerOn && !devicePending.Any(c => c.Action == "Off"))
            {
                Clear(device); // The user paused a throttled car; do not start it for them.
                continue;
            }
            if (device.Present && device.State == "on" && device.RestorePowerOn && !devicePending.Any(c => c.Action == "On"))
            {
                device.RestorePowerOn = false;
                if (device.RestoreCurrentAmps is null) { Clear(device); continue; }
            }
            if (device.RestoreStatus == "restoring" && restoring is not null && CommandPolicy.Terminal(restoring))
                device.RestoreStatus = "failed";
            // The off timer and capacity timer run concurrently. An EV still on needs only the capacity timer.
            var fits = device.RestorePowerOn ? Fits(home, device, limitWatts) : EvChargingPolicy.Increase(home, device, limitWatts) is not null;
            if (pending || device.RestoreStatus != "waiting" || !fits)
                device.RestoreEligibleSinceUtc = null;
            else
                device.RestoreEligibleSinceUtc ??= now;
        }
        if (pending || home.LastRestoreUtc > now - BetweenRestorations) return;
        var next = Ordered(home.Devices.Where(d => d.RestoreQueuedUtc != null && d.RestoreStatus == "waiting" &&
            d.RestoreEligibleSinceUtc <= now - StableCapacityTime && CanRestore(home, d, now, limitWatts))).FirstOrDefault();
        if (next is null) return;
        var increase = !next.RestorePowerOn;
        var ampsTarget = increase ? EvChargingPolicy.Increase(home, next, limitWatts) : null;
        home.Commands.Add(new DeviceCommand
        {
            HomeId = home.Id, EntityId = next.EntityId, RequestedBy = home.OwnerId,
            Action = increase ? "SetCurrent" : "On", Automatic = true, IsRestoration = true,
            CurrentControlEntityId = increase ? next.EvCurrentEntityId : null, CurrentAmps = ampsTarget,
            EstimatedWatts = Estimate(home, next),
            IdempotencyKey = $"restore:{Guid.NewGuid():N}", CreatedUtc = now,
            ExpiresUtc = increase ? now.AddSeconds(30) : now.AddMinutes(2), NextAttemptUtc = now,
            Message = increase ? $"Restoring EV charging toward {next.RestoreCurrentAmps:0.##} A; increasing to {ampsTarget:0.##} A within available capacity."
                : "Restoring one queued device after sustained spare capacity."
        });
        next.RestoreStatus = "restoring";
        home.LastRestoreUtc = now;
        ResetEligibility(home);
    }
    public static void Confirm(Home home, Device device, DeviceCommand command, DateTime now)
    {
        command.Status = "Confirmed";
        command.Message = $"Home Assistant state confirms the device is {(command.Action == "On" ? "on" : "off")}.";
        if (command.Action == "On")
        {
            device.RestorePowerOn = false;
            if (device.RestoreCurrentAmps is null) Clear(device);
            else
            {
                device.RestoreStatus = "waiting";
                device.RestoreEligibleSinceUtc = null;
                device.RestoreAtFront = true;
            }
            home.LastRestoreUtc = now;
        }
        else
        {
            device.RestorePowerOn = true;
            device.RestoreWatts = command.EstimatedWatts;
            device.RestoreQueuedUtc = now;
            device.RestoreEligibleSinceUtc = null;
            device.RestoreStatus = "waiting";
        }
    }
}
