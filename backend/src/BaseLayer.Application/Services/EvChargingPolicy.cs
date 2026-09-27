using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public static class EvChargingPolicy
{
    public const double HeadroomWatts = 100;
    public static CurrentControlDto? Control(Home home, Device device) =>
        (JsonSerializer.Deserialize<List<CurrentControlDto>>(home.CurrentControlsJson) ?? []).SingleOrDefault(c => c.EntityId == device.EvCurrentEntityId);

    public static double? Increase(Home home, Device device, double limitWatts)
    {
        var control = Control(home, device);
        if (!home.SmartPowerOffEnabled || !device.Allowed || !device.Present || device.State != "on" || device.SmartUsageHeld ||
            device.ShutoffLevel != ShutoffLevels.Anytime || device.EvCurrentEntityId is null ||
            home.HouseholdWatts is not { } total || !double.IsFinite(total) || total < 0 ||
            device.PowerWatts is not { } power || !double.IsFinite(power) || power < 0 || power > total ||
            !double.IsFinite(device.EvWattsPerAmp) || device.EvWattsPerAmp <= 0 ||
            device.RestoreEntry?.TargetCurrentAmps is not { } original || control?.Amps is not { } current ||
            !Valid(control, current) || !Valid(control, original) || current >= original) return null;
        // Leave 500 W spare and budget the full rated draw, even when the car is tapering.
        if (home.GridOutageRisk == GridOutageRisk.Low) return original;
        var available = limitWatts - 500 - (total - power);
        var target = control.Min + Math.Floor((available / device.EvWattsPerAmp - control.Min) / control.Step) * control.Step;
        target = Math.Min(target, original);
        return target > current && Valid(control, target) ? target : null;
    }
    public static bool Valid(CurrentControlDto control, double amps) =>
        double.IsFinite(amps) && double.IsFinite(control.Min) && double.IsFinite(control.Max) &&
        double.IsFinite(control.Step) && control.Min >= 0 && control.Max > control.Min && control.Step > 0 &&
        amps >= control.Min && amps <= control.Max &&
        Math.Abs((amps - control.Min) / control.Step - Math.Round((amps - control.Min) / control.Step)) < 0.000001;

    public static double? Reduction(Home home, Device device, CurrentControlDto? control, double limitWatts)
    {
        if (!home.SmartPowerOffEnabled || !GridOutageRisk.RequiresReduction(home.GridOutageRisk) || !device.Allowed || !device.Present || device.State != "on" ||
            device.ShutoffLevel != ShutoffLevels.Anytime || device.EvCurrentEntityId is null ||
            home.HouseholdWatts is not { } total || !double.IsFinite(total) || total < limitWatts ||
            device.PowerWatts is not { } power || !double.IsFinite(power) || power <= 0 || power > total ||
            !double.IsFinite(device.EvWattsPerAmp) || device.EvWattsPerAmp <= 0 ||
            control?.Amps is not { } current || !Valid(control, current)) return null;

        // Use the mapped charger's electrical rating, not a potentially tapered
        // charging measurement, to bound its draw at the new current limit.
        var available = limitWatts - HeadroomWatts - (total - power);
        var target = control.Min + Math.Floor((available / device.EvWattsPerAmp - control.Min) / control.Step) * control.Step;
        target = Math.Min(target, current - control.Step);
        return target > 0 && Valid(control, target) ? target : null;
    }
}
