using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public static class PowerSupplyDetection
{
    public static string Normalize(string state) => state.ToLowerInvariant() switch
    {
        "grid" => "grid",
        "battery" => "battery",
        "pv" => "solar",
        "generator" => "generator",
        "none" => "none",
        _ => "unknown"
    };

    public static PowerSupplyStatusDto Current(Home home, DateTime now)
    {
        var sources = JsonSerializer.Deserialize<List<PowerSupplyReading>>(home.PowerSupplyJson) ?? [];
        var fresh = !home.Revoked && home.LastSeenUtc >= now.AddSeconds(-20);
        // Conflicting panels or any missing signal cannot establish a whole-home mode.
        var states = sources.Select(s => s.State).Distinct().ToList();
        var state = fresh && states.Count == 1 ? states[0] : "unknown";
        bool? battery = state switch
        {
            "battery" => true,
            "grid" or "solar" or "generator" or "none" => false,
            _ => null
        };
        return new(battery is null ? "unknown" : state, battery, home.LastSeenUtc,
            fresh ? sources : sources.Select(s => s with { State = "unknown" }).ToList());
    }
}
