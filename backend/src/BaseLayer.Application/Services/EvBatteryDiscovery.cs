using System.Text.RegularExpressions;
using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public static class EvBatteryDiscovery
{
    private static readonly HashSet<string> Descriptors = new(StringComparer.Ordinal)
    {
        "virtual", "lab", "battery", "charge", "charging", "charger", "state", "of",
        "soc", "target", "level", "percent", "percentage", "capacity", "usable", "current", "limit", "power", "kwh", "wh"
    };
    private static string Identity(string value) => string.Join("_", Regex.Matches(value.ToLowerInvariant(), "[a-z0-9]+")
        .Select(m => m.Value).Where(w => !Descriptors.Contains(w)).Distinct().Order());
    private static string EntityIdentity(string id) => Identity(id.Contains('.') ? id[(id.IndexOf('.') + 1)..] : id);

    public static bool Matches(string id, string name, string otherId, string otherName) =>
        (EntityIdentity(id) is { Length: > 0 } key && key == EntityIdentity(otherId)) ||
        (Identity(name) is { Length: > 0 } label && label == Identity(otherName));

    public static Dictionary<string, EvBatterySettings> Resolve(IEnumerable<Device> devices, List<EvBatterySensorDto> sensors,
        Func<Device, EvBatterySettings?> savedSettings)
    {
        var chargers = devices.Where(d => d.Present && d.EvCurrentEntityId != null).ToList();
        var saved = devices.Select(savedSettings).OfType<EvBatterySettings>().Select(s => s.SensorEntityId).ToHashSet();
        var candidates = chargers.Where(d => savedSettings(d) is null).ToDictionary(d => d.EntityId,
            d => sensors.Where(s => !saved.Contains(s.EntityId) && Matches(d.EntityId, d.Name, s.EntityId, s.Name)).ToList());
        // Both ends must be unique. Do not assign one vehicle's battery to two chargers.
        return candidates.Where(pair => pair.Value.Count == 1 && candidates.Count(other => other.Value.Any(s => s.EntityId == pair.Value[0].EntityId)) == 1)
            .ToDictionary(pair => pair.Key, pair => new EvBatterySettings(pair.Value[0].EntityId, pair.Value[0].CapacityKwh, AutoDetected: true));
    }
}
