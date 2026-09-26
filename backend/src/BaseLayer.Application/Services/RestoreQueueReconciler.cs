using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

// Reconcile observations even while another command or the settling timer blocks scheduling.
internal static class RestoreQueueReconciler
{
    public static void Reconcile(Home home)
    {
        var commandsByDevice = home.Commands.ToLookup(c => c.EntityId);
        foreach (var device in home.Devices.Where(d => d.RestoreEntry is not null))
        {
            if (!device.Allowed || device.ShutoffLevel == ShutoffLevels.Never)
            {
                AutoRestorePolicy.Clear(device);
                continue;
            }
            var commands = commandsByDevice[device.EntityId].ToList();
            var pending = commands.Where(c => !CommandPolicy.Terminal(c)).ToList();
            ReconcileCurrentTarget(home, device, commands, pending);
            if (device.RestoreEntry is null) continue;
            ReconcilePowerState(device, pending);
            if (device.RestoreEntry is not { } entry) continue;

            var lastRestore = commands.Where(c => c.Action == "On" || c.IsRestoration)
                .OrderByDescending(c => c.CreatedUtc).FirstOrDefault();
            if (lastRestore is not null && CommandPolicy.Terminal(lastRestore))
                entry.FailRestoration();
        }
    }

    private static void ReconcileCurrentTarget(Home home, Device device,
        List<DeviceCommand> commands, List<DeviceCommand> pending)
    {
        if (device.RestoreEntry is not { TargetCurrentAmps: { } target } entry ||
            pending.Any(c => c.Action == "SetCurrent") ||
            EvChargingPolicy.Control(home, device)?.Amps is not { } amps) return;

        var lastCurrent = commands.Where(c => c.Action == "SetCurrent" && c.Automatic && c.Attempts > 0)
            .OrderByDescending(c => c.CreatedUtc).FirstOrDefault();
        var failedUnchanged = lastCurrent is { Status: "Failed" or "Expired" or "Cancelled", PreviousCurrentAmps: { } previous }
            && Math.Abs(amps - previous) < 0.000001;
        var manualOverride = !failedUnchanged && entry.LastManagedCurrentAmps is { } managed && Math.Abs(amps - managed) > 0.000001;
        if (amps >= target || manualOverride)
            AutoRestorePolicy.ClearCurrentRestore(device);
    }

    private static void ReconcilePowerState(Device device, List<DeviceCommand> pending)
    {
        var entry = device.RestoreEntry!;
        if (!device.Present) return;
        if (device.State == "off" && !entry.PowerOn && !pending.Any(c => c.Action == "Off"))
        {
            AutoRestorePolicy.Clear(device); // The user paused a throttled car; do not restart it.
            return;
        }
        if (device.State == "on" && entry.PowerOn && !pending.Any(c => c.Action == "On"))
        {
            entry.PowerOn = false;
            if (entry.TargetCurrentAmps is null) AutoRestorePolicy.Clear(device);
        }
    }
}
