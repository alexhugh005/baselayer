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
        entry.EligibleSinceUtc = null;
        if (!entry.PowerOn) Clear(device);
    }
    public static double? Estimate(Home home, Device device) => device.RestoreEntry is { TargetCurrentAmps: { } target, PowerOn: false }
        ? EvChargingPolicy.Control(home, device)?.Amps is { } current ? Math.Max(0, target - current) * device.EvWattsPerAmp : null
        : device.RestoreEntry?.EstimatedWatts;

    public static void ResetEligibility(Home home)
    {
        foreach (var device in home.Devices)
            if (device.RestoreEntry is { } entry) entry.EligibleSinceUtc = null;
    }
    public static bool Fits(Home home, Device device, double limitWatts) =>
        home.SmartPowerOffEnabled && !SmartPanelCircuit.IsCircuit(device.EntityId) && !device.SmartUsageHeld && device.Present && device.Allowed && device.ShutoffLevel != ShutoffLevels.Never &&
        !device.EntityId.StartsWith("climate.") && device.State == "off" && device.RestoreEntry is { PowerOn: true } entry &&
        home.HouseholdWatts is { } current && double.IsFinite(current) && current >= 0 &&
        entry.EstimatedWatts is { } estimate && double.IsFinite(estimate) && estimate > 0 &&
        current + estimate + Math.Max(500, estimate * 0.1) < limitWatts;

    private static bool CanRestore(Home home, Device device, DateTime now, double limitWatts) =>
        device.RestoreEntry is { } entry && (entry.PowerOn
            ? entry.QueuedUtc <= now - MinimumOffTime && Fits(home, device, limitWatts)
            : EvChargingPolicy.Increase(home, device, limitWatts) is not null);

    public static void QueueNext(Home home, DateTime now, double limitWatts)
    {
        var pending = home.Commands.Any(c => !CommandPolicy.Terminal(c));
        foreach (var device in home.Devices.Where(d => d.RestoreEntry != null))
        {
            var entry = device.RestoreEntry!;
            var restoring = home.Commands.Where(c => c.EntityId == device.EntityId && (c.Action == "On" || c.IsRestoration)).OrderByDescending(c => c.CreatedUtc).FirstOrDefault();
            if (!device.Allowed || device.ShutoffLevel == ShutoffLevels.Never)
            {
                Clear(device);
                continue;
            }
            var devicePending = home.Commands.Where(c => c.EntityId == device.EntityId && !CommandPolicy.Terminal(c)).ToList();
            if (entry.TargetCurrentAmps is { } target && !devicePending.Any(c => c.Action == "SetCurrent") &&
                EvChargingPolicy.Control(home, device)?.Amps is { } amps)
            {
                var lastCurrent = home.Commands.Where(c => c.EntityId == device.EntityId && c.Action == "SetCurrent" && c.Automatic && c.Attempts > 0)
                    .OrderByDescending(c => c.CreatedUtc).FirstOrDefault();
                var failedUnchanged = lastCurrent is { Status: "Failed" or "Expired" or "Cancelled", PreviousCurrentAmps: { } previous } && Math.Abs(amps - previous) < 0.000001;
                if (amps >= target || (!failedUnchanged && entry.LastManagedCurrentAmps is { } managed && Math.Abs(amps - managed) > 0.000001))
                    ClearCurrentRestore(device); // Target reached externally or a manual override: relinquish ownership.
            }
            if (device.RestoreEntry is null) continue;
            if (device.Present && device.State == "off" && !entry.PowerOn && !devicePending.Any(c => c.Action == "Off"))
            {
                Clear(device); // The user paused a throttled car; do not start it for them.
                continue;
            }
            if (device.Present && device.State == "on" && entry.PowerOn && !devicePending.Any(c => c.Action == "On"))
            {
                entry.PowerOn = false;
                if (entry.TargetCurrentAmps is null) { Clear(device); continue; }
            }
            if (entry.Status == "restoring" && restoring is not null && CommandPolicy.Terminal(restoring))
                entry.Status = "failed";
            // The off timer and capacity timer run concurrently. An EV still on needs only the capacity timer.
            var fits = entry.PowerOn ? Fits(home, device, limitWatts) : EvChargingPolicy.Increase(home, device, limitWatts) is not null;
            if (pending || entry.Status != "waiting" || !fits)
                entry.EligibleSinceUtc = null;
            else
                entry.EligibleSinceUtc ??= now;
        }
        if (pending || home.LastRestoreUtc > now - BetweenRestorations) return;
        var next = Ordered(home.Devices.Where(d => d.RestoreEntry != null && d.RestoreEntry!.Status == "waiting" &&
            d.RestoreEntry!.EligibleSinceUtc <= now - StableCapacityTime && CanRestore(home, d, now, limitWatts))).FirstOrDefault();
        if (next is null) return;
        var increase = !next.RestoreEntry!.PowerOn;
        var ampsTarget = increase ? EvChargingPolicy.Increase(home, next, limitWatts) : null;
        home.Commands.Add(new DeviceCommand
        {
            HomeId = home.Id, EntityId = next.EntityId, RequestedBy = home.OwnerId,
            Action = increase ? "SetCurrent" : "On", Automatic = true, IsRestoration = true,
            CurrentControlEntityId = increase ? next.EvCurrentEntityId : null, CurrentAmps = ampsTarget,
            EstimatedWatts = Estimate(home, next),
            IdempotencyKey = $"restore:{Guid.NewGuid():N}", CreatedUtc = now,
            ExpiresUtc = increase ? now.AddSeconds(30) : now.AddMinutes(2), NextAttemptUtc = now,
            Message = increase ? $"Restoring EV charging toward {next.RestoreEntry!.TargetCurrentAmps:0.##} A; increasing to {ampsTarget:0.##} A within available capacity."
                : "Restoring one queued device after sustained spare capacity."
        });
        next.RestoreEntry!.Status = "restoring";
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
                    entry.Status = "waiting";
                    entry.EligibleSinceUtc = null;
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
            entry.EligibleSinceUtc = null;
            entry.Status = "waiting";
        }
    }
}
