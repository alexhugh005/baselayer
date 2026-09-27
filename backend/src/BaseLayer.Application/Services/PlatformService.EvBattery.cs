using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed partial class PlatformService
{
    private static List<EvBatterySensorDto> EvBatterySensors(Home home) =>
        JsonSerializer.Deserialize<List<EvBatterySensorDto>>(home.EvBatterySensorsJson) ?? [];
    private static EvBatterySettings? EvBattery(Device device) => device.EvBatterySettingsJson is { } json
        ? JsonSerializer.Deserialize<EvBatterySettings>(json) : null;

    public Task<HomeDto> EvBatterySettingsAsync(string owner, Guid id, EvBatterySettingsRequest request) =>
        operations.RunAsync(id, () => repository.TransactionAsync(async () =>
        {
            var home = await Owned(owner, id);
            if (home.Revoked) throw new ArgumentException("Connection revoked.");
            var device = home.Devices.SingleOrDefault(d => d.Present && d.EntityId == request.EntityId && d.EvCurrentEntityId != null);
            if (device is null) throw new ArgumentException("Pair an EV charging current control in Settings first.");
            if (request.Settings is { } settings)
            {
                if (!EvBatterySensors(home).Any(s => s.EntityId == settings.SensorEntityId) && EvBattery(device)?.SensorEntityId != settings.SensorEntityId)
                    throw new ArgumentException("Choose a discovered battery percentage sensor.");
                if (settings.CapacityKwh is not { } capacity || !double.IsFinite(capacity) || capacity <= 0 || capacity > 1000 ||
                    !double.IsFinite(settings.EfficiencyPercent) || settings.EfficiencyPercent <= 0 || settings.EfficiencyPercent > 100)
                    throw new ArgumentException("Enter a capacity above 0 and up to 1,000 kWh and efficiency above 0 and up to 100%.");
                if (home.Devices.Any(d => d.EntityId != device.EntityId && EvBattery(d)?.SensorEntityId == settings.SensorEntityId))
                    throw new ArgumentException("This battery sensor is already paired with another EV.");
            }
            device.EvBatterySettingsJson = request.Settings is null ? null : JsonSerializer.Serialize(request.Settings with { AutoDetected = false });
            await repository.SaveAsync();
            return ToDto(home);
        }));
}
