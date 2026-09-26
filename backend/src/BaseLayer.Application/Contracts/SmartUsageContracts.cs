namespace BaseLayer.Application.Contracts;

public sealed record SmartUsageChange(string EntityId, string Name, string Action, double EstimatedWatts, string ShutoffLevel);
public sealed record SmartUsageExcludedDevice(string EntityId, string Name, string Reason);
public sealed record SmartUsagePlan(
    Guid HomeId, BatteryStatusDto Battery, DateTimeOffset CalculatedAtUtc,
    double CurrentWatts, double AllOnWatts, double MinimumWatts, double TargetWatts, double ProjectedWatts,
    double? CurrentHours, double? ShortestHours, double? LongestHours, double? ProjectedHours,
    double LimitWatts, List<SmartUsageChange> Changes, List<SmartUsageExcludedDevice> ExcludedDevices,
    bool CanApply, string? BlockedReason, string Revision);
public sealed record ApplySmartUsageRequest(double TargetWatts, string Revision, string IdempotencyKey);
