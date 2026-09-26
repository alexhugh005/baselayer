namespace BaseLayer.Domain.Entities;

public static class SmartPanelCircuit
{
    // Entity naming convention used by the installed SPAN integration.
    public static bool IsCircuit(string entityId) =>
        System.Text.RegularExpressions.Regex.IsMatch(entityId, @"^switch\.span_panel_[a-z0-9_]+_breaker$");

    public static bool IsPriorityControl(string entityId) =>
        System.Text.RegularExpressions.Regex.IsMatch(entityId, @"^select\.span_panel_[a-z0-9_]+_circuit_priority$");

    public static string PriorityEntityId(string circuitId) =>
        "select." + circuitId["switch.".Length..^"_breaker".Length] + "_circuit_priority";

    public static bool ValidPriority(string? priority) => priority is "never" or "soc_threshold" or "off_grid";
}
