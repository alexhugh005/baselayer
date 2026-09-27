using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

// A proposal only: creating or selecting a plan never changes a device or sends a command.
public sealed record RestorationPlan(
    Device Device,
    string Action,
    double? EstimatedWatts,
    DateTime NotBeforeUtc,
    TimeSpan Timeout,
    string Message,
    string? CurrentControlEntityId = null,
    double? CurrentAmps = null);

public interface IRestorationStrategy
{
    RestorationPlan? Plan(Home home, Device device, DateTime now, double limitWatts);
}

public sealed class PowerOnRestoration : IRestorationStrategy
{
    public static bool Fits(Home home, Device device, double limitWatts) =>
        home.SmartPowerOffEnabled && !SmartPanelCircuit.IsCircuit(device.EntityId) && !device.SmartUsageHeld && device.Present && device.Allowed && device.ShutoffLevel != ShutoffLevels.Never &&
        !device.EntityId.StartsWith("climate.") && device.State == "off" && device.RestoreEntry is { PowerOn: true } entry &&
        home.HouseholdWatts is { } current && double.IsFinite(current) && current >= 0 &&
        entry.EstimatedWatts is { } estimate && double.IsFinite(estimate) && estimate > 0 &&
        (home.GridOutageRisk == GridOutageRisk.Low || current + estimate + Math.Max(500, estimate * 0.1) < limitWatts);

    public RestorationPlan? Plan(Home home, Device device, DateTime now, double limitWatts)
    {
        if (!Fits(home, device, limitWatts)) return null;
        var entry = device.RestoreEntry!;
        // Capacity can stabilize while the minimum off timer is still running.
        return new(device, "On", entry.EstimatedWatts, entry.QueuedUtc + AutoRestorePolicy.MinimumOffTime,
            TimeSpan.FromMinutes(2), "Restoring one queued device after sustained spare capacity.");
    }
}

public sealed class EvCurrentRestoration : IRestorationStrategy
{
    public RestorationPlan? Plan(Home home, Device device, DateTime now, double limitWatts)
    {
        if (device.RestoreEntry is not { PowerOn: false, TargetCurrentAmps: { } original }) return null;
        if (EvChargingPolicy.Increase(home, device, limitWatts) is not { } amps) return null;
        var estimatedWatts = EvChargingPolicy.Control(home, device)?.Amps is { } current
            ? (double?)Math.Max(0, original - current) * device.EvWattsPerAmp : null;
        return new(device, "SetCurrent", estimatedWatts, now, TimeSpan.FromSeconds(30),
            $"Restoring EV charging toward {original:0.##} A; increasing to {amps:0.##} A within available capacity.",
            device.EvCurrentEntityId, amps);
    }
}
