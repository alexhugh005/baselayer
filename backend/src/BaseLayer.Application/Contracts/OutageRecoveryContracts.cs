namespace BaseLayer.Application.Contracts;

public sealed record OutageDeviceDto(string EntityId, string Name, string TargetState, string Status);
public sealed record OutageCircuitDto(string EntityId, string Name, double? EstimatedWatts, string Status, List<OutageDeviceDto>? Devices = null);
public sealed record OutageRecoveryStatusDto(string Status, string? EventId, DateTime? StartedUtc,
    DateTime? NextRestoreAtUtc, double BudgetWatts, double? MeasuredWatts, double? ReservedWatts,
    List<OutageCircuitDto> Circuits);
