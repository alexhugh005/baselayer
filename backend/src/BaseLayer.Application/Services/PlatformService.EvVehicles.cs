using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed partial class PlatformService
{
    private static List<EvVehicleDto> EvVehicles(Home home)
    {
        var vehicles = JsonSerializer.Deserialize<List<EvVehicleDto>>(home.EvVehiclesJson) ?? [];
        var detected = EvBatteryDiscovery.Resolve(home.Devices, EvBatterySensors(home), EvBattery);
        // Keep existing pairings visible until they are saved as named vehicle profiles.
        foreach (var device in home.Devices.Where(d => d.Present && d.EvCurrentEntityId != null))
            if (!vehicles.Any(v => v.ChargerEntityId == device.EntityId) &&
                (EvBattery(device) ?? detected.GetValueOrDefault(device.EntityId)) is { } battery)
                vehicles.Add(new(device.EntityId, device.Name, device.EntityId, battery));
        return vehicles;
    }

    public Task<HomeDto> SaveEvVehicleAsync(string owner, Guid id, EvVehicleDto request) =>
        operations.RunAsync(id, () => repository.TransactionAsync(async () =>
        {
            var home = await Owned(owner, id);
            if (home.Revoked) throw new ArgumentException("Connection revoked.");
            var vehicles = EvVehicles(home);
            var previous = vehicles.SingleOrDefault(v => v.Id == request.Id);
            if (string.IsNullOrWhiteSpace(request.Id) || (previous is null && !Guid.TryParse(request.Id, out _)) ||
                string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
                throw new ArgumentException("Enter a vehicle name up to 100 characters and a valid vehicle ID.");
            if (!home.Devices.Any(d => d.Present && d.EntityId == request.ChargerEntityId && d.EvCurrentEntityId != null))
                throw new ArgumentException("Choose a paired EV charger.");
            var battery = request.Battery;
            if (battery is null || (!EvBatterySensors(home).Any(s => s.EntityId == battery.SensorEntityId) && previous?.Battery.SensorEntityId != battery.SensorEntityId))
                throw new ArgumentException("Choose a discovered battery percentage sensor.");
            if (battery.CapacityKwh is not { } capacity || !double.IsFinite(capacity) || capacity <= 0 || capacity > 1000 ||
                !double.IsFinite(battery.EfficiencyPercent) || battery.EfficiencyPercent <= 0 || battery.EfficiencyPercent > 100)
                throw new ArgumentException("Enter a capacity above 0 and up to 1,000 kWh and efficiency above 0 and up to 100%.");
            if (vehicles.Any(v => v.Id != request.Id && v.ChargerEntityId != request.ChargerEntityId && v.Battery.SensorEntityId == battery.SensorEntityId))
                throw new ArgumentException("This battery sensor is already assigned to a different charger.");
            var saved = request with { Name = request.Name.Trim(), Battery = battery with { AutoDetected = false } };
            if (previous is not null) vehicles.Remove(previous);
            vehicles.Add(saved);
            home.EvVehiclesJson = JsonSerializer.Serialize(vehicles);
            if (previous != saved)
                foreach (var command in home.Commands.Where(c => c.EvVehicleId == request.Id && !CommandPolicy.Terminal(c)))
                {
                    command.Status = "Cancelled";
                    command.Message = "Vehicle settings changed; apply a new charging plan.";
                }
            await repository.SaveAsync();
            return ToDto(home);
        }));
}
