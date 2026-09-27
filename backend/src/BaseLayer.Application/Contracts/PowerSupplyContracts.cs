namespace BaseLayer.Application.Contracts;

public sealed record PowerSupplyReading(string EntityId, string State);

/// <summary>Reported backup mode, independent of energy usage and predicted outage risk.</summary>
public sealed record PowerSupplyStatusDto(
    string State,
    bool? IsOnBattery,
    DateTime? ObservedAtUtc,
    List<PowerSupplyReading> Sources);
