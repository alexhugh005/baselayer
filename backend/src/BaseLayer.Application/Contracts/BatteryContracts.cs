namespace BaseLayer.Application.Contracts;

/// <summary>Energy is measured in kWh; state of charge is a percentage from 0 to 100.</summary>
public sealed record BatterySnapshot(
    double CapacityKwh,
    double StoredEnergyKwh,
    DateTimeOffset ObservedAtUtc,
    bool IsSimulated);

public sealed record BatteryStatusDto(
    Guid HomeId,
    double CapacityKwh,
    double StoredEnergyKwh,
    double StateOfChargePercent,
    DateTimeOffset ObservedAtUtc,
    bool IsSimulated);
