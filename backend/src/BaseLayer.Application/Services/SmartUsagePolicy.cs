using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;
namespace BaseLayer.Application.Services;

public static class SmartUsagePolicy
{
    // The current battery provider models one battery per home.
    public const double BatteryLimitWatts = 11000;
    public static bool Positive(double? value) => value is { } watts && double.IsFinite(watts) && watts > 0;
    public static bool Controllable(Device device) => device.Present && device.Allowed && !SmartPanelCircuit.IsCircuit(device.EntityId) &&
        device.ShutoffLevel is ShutoffLevels.Anytime or ShutoffLevels.Sometimes &&
        device.EntityId.Split('.')[0] is "switch" or "light" or "fan";
    public static double? OnEstimate(Device device) => Positive(device.LastOnWatts) ? device.LastOnWatts : device.RestoreEntry?.EstimatedWatts;
    public static double? Hours(double energyKwh, double watts) => energyKwh == 0 ? 0 : watts == 0 ? null : energyKwh / (watts / 1000);

    public static bool CanTurnOn(Home home, Device device, DeviceCommand command) =>
        Controllable(device) && device.State == "off" && Positive(command.EstimatedWatts) &&
        home.HouseholdWatts is { } current && double.IsFinite(current) && current >= 0 &&
        device.PowerWatts is { } standby && double.IsFinite(standby) && standby >= 0 &&
        Positive(OnEstimate(device)) && current + Math.Max(0, Math.Max(command.EstimatedWatts!.Value, OnEstimate(device)!.Value) - standby)
            <= Math.Min(BatteryLimitWatts, command.UsageBudgetWatts ?? BatteryLimitWatts);

    public static SmartUsagePlan Plan(Home home, BatteryStatusDto battery, DateTimeOffset now, double? targetWatts)
    {
        if (home.Revoked || home.LastSeenUtc is null || home.LastSeenUtc < now.UtcDateTime.AddSeconds(-20) ||
            home.HouseholdWatts is not { } current || !double.IsFinite(current) || current < 0)
            throw new ArgumentException("Fresh household power readings are required to plan Smart Usage.");
        if (battery.ObservedAtUtc < now.AddSeconds(-30) || battery.ObservedAtUtc > now.AddSeconds(5))
            throw new ArgumentException("Fresh battery telemetry is required to plan Smart Usage.");

        var excluded = new List<SmartUsageExcludedDevice>();
        var loads = new List<(Device Device, bool On, double Watts)>();
        foreach (var device in home.Devices.Where(d => d.Present).OrderBy(d => d.EntityId, StringComparer.Ordinal))
        {
            string? reason = !Controllable(device) ? "Not permitted for Smart Usage switching."
                : device.State is not ("on" or "off") ? "Device state is unavailable."
                : device.PowerWatts is not { } measured || !double.IsFinite(measured) || measured < 0 ? "Power reading is unavailable."
                : device.State == "on" && !Positive(device.PowerWatts) ? "No positive running power reading."
                : device.State == "off" && (!Positive(OnEstimate(device)) || OnEstimate(device) <= device.PowerWatts) ? "No saved running power estimate. Use the device with its power sensor connected first."
                : null;
            if (reason is not null) { excluded.Add(new(device.EntityId, device.Name, reason)); continue; }
            loads.Add((device, device.State == "on", device.State == "on" ? device.PowerWatts!.Value : OnEstimate(device)!.Value - device.PowerWatts!.Value));
        }
        var minimum = current - loads.Where(d => d.On).Sum(d => d.Watts);
        var allOn = current + loads.Where(d => !d.On).Sum(d => d.Watts);
        if (minimum < -1 || !double.IsFinite(allOn))
            throw new ArgumentException("Device power readings exceed household usage. Check for overlapping power sensors.");
        minimum = Math.Max(0, minimum);
        var target = targetWatts ?? current;
        if (!double.IsFinite(target) || target < 0)
            throw new ArgumentException("Choose a valid target power level.");
        target = Math.Clamp(target, minimum, allOn);
        var projected = current;
        var changes = new List<SmartUsageChange>();
        if (target < current)
        {
            // Largest first is the minimum-cardinality solution for a required reduction.
            foreach (var load in loads.Where(d => d.On).OrderByDescending(d => d.Watts).ThenBy(d => d.Device.EntityId, StringComparer.Ordinal))
            {
                if (projected <= target + 0.001) break;
                projected = Math.Max(minimum, projected - load.Watts);
                changes.Add(new(load.Device.EntityId, load.Device.Name, "Off", load.Watts, load.Device.ShutoffLevel));
            }
        }
        else if (target > current)
        {
            foreach (var load in loads.Where(d => !d.On).OrderByDescending(d => d.Watts).ThenBy(d => d.Device.EntityId, StringComparer.Ordinal))
            {
                if (projected + load.Watts > target + 0.001) continue;
                projected += load.Watts;
                changes.Add(new(load.Device.EntityId, load.Device.Name, "On", OnEstimate(load.Device)!.Value, load.Device.ShutoffLevel));
            }
        }
        var blocked = home.Commands.Any(c => !CommandPolicy.Terminal(c)) ? "Wait for pending device changes to finish."
            : battery.StoredEnergyKwh <= 0 ? "The battery is empty."
            : projected > BatteryLimitWatts ? "This selection exceeds the 11 kW limit per battery. Choose a longer runtime."
            : home.SmartPowerOffEnabled && GridOutageRisk.RequiresReduction(home) && projected == BatteryLimitWatts ? "Smart Shutoff activates at 11 kW. Choose a longer runtime to stay below its threshold."
            : loads.Count == 0 ? "No measured, permitted devices are available to plan." : null;
        var revisionData = new { home.Id, home.HouseholdWatts, Target = target,
            Devices = home.Devices.OrderBy(d => d.EntityId, StringComparer.Ordinal).Select(d => new
            { d.EntityId, d.Present, d.State, d.PowerWatts, d.LastOnWatts, RestoreWatts = d.RestoreEntry?.EstimatedWatts, d.Allowed, d.ShutoffLevel, d.SmartUsageHeld }), changes };
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(revisionData))));
        return new(home.Id, battery, now, current, allOn, minimum, target, projected,
            Hours(battery.StoredEnergyKwh, current), Hours(battery.StoredEnergyKwh, allOn),
            Hours(battery.StoredEnergyKwh, minimum), Hours(battery.StoredEnergyKwh, projected),
            BatteryLimitWatts, changes, excluded, blocked is null, blocked, revision);
    }
}
