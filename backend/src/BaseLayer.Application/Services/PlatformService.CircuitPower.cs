using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed partial class PlatformService
{
    private bool CircuitExceedsBatteryLimit(Home home, Device device, ProviderSnapshot snapshot)
    {
        // Report this specific reason only when fresh telemetry establishes it.
        // Unknown supply/load and other provider failures retain their own errors.
        if (PowerSupplyDetection.Current(home, Now).IsOnBattery != true ||
            snapshot.CircuitRestoreEstimates?.TryGetValue(device.EntityId, out var estimate) != true ||
            !double.IsFinite(estimate) || estimate < 0)
            return false;

        var circuits = home.Devices.Where(d => d.Present && SmartPanelCircuit.IsCircuit(d.EntityId)).ToList();
        double? circuitWatts = circuits.Count > 0 && circuits.All(d => d.PowerWatts is { } watts && double.IsFinite(watts) && watts >= 0)
            ? circuits.Sum(d => d.PowerWatts!.Value) : null;
        var householdWatts = home.HouseholdWatts is { } total && double.IsFinite(total) && total >= 0
            ? home.HouseholdWatts : null;
        if (circuitWatts is null && householdWatts is null) return false;
        var measured = Math.Max(circuitWatts ?? 0, householdWatts ?? 0);
        return measured + estimate > SmartUsagePolicy.BatteryLimitWatts;
    }
}
