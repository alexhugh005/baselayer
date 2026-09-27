using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public static class DevicePowerStandards
{
    public const double TriggerMultiplier = 1.5;
    // Editable starting estimates, not appliance ratings. See docs/anomaly-savings.md.
    public static IReadOnlyList<DeviceCategoryDto> Categories { get; } = Array.AsReadOnly<DeviceCategoryDto>([
        new("toaster", "Toaster", 1200),
        new("microwave", "Microwave", 1100),
        new("coffee-maker", "Coffee maker", 1200),
        new("refrigerator", "Refrigerator / freezer", 725),
        new("dishwasher", "Dishwasher", 2400),
        new("washer", "Clothes washer", 500),
        new("dryer", "Electric clothes dryer", 5000),
        new("space-heater", "Space heater", 1500),
        new("water-heater", "Electric water heater", 5500),
        new("television", "Television", 120),
        new("computer", "Desktop computer and monitor", 270),
        new("ceiling-fan", "Ceiling fan", 175),
        new("custom", "Other / custom", null)
    ]);

    public static double? StandardWatts(Device device) => device.StandardWattsOverride ??
        Categories.SingleOrDefault(c => c.Id == device.Category)?.StandardWatts;

    public static bool Exceeds(double observed, double standard) =>
        double.IsFinite(observed) && double.IsFinite(standard) && standard > 0 && observed >= standard * TriggerMultiplier;

    public static void Validate(Home home, Dictionary<string, DevicePowerSettings?>? settings)
    {
        if (settings is null) return;
        foreach (var (entityId, config) in settings)
        {
            if (!home.Devices.Any(d => d.Present && d.EntityId == entityId) ||
                config is not null && (!Categories.Any(c => c.Id == config.Category) ||
                    config.StandardWatts is { } watts && (!double.IsFinite(watts) || watts < 1 || watts > 1_000_000) ||
                    config.Category == "custom" && config.StandardWatts is null))
                throw new ArgumentException("Choose a discovered device and category, with standard power from 1 to 1,000,000 W. Custom categories require standard power.");
        }
    }
}
