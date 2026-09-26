namespace BaseLayer.Application.Contracts;

public sealed record UsageSeriesDto(Guid Id, string PowerSensorEntityId, DateTime StartedUtc, DateTime? EndedUtc);
public sealed record UsageBucketDto(Guid MeasurementSeriesId, DateTime BucketStartUtc,
    double? AverageWatts, double? MinWatts, double? MaxWatts, double? EstimatedEnergyWh,
    double CoveredSeconds, double CoveragePercent, int SampleCount);
public sealed record DeviceUsageHistoryDto(Guid HomeId, Guid DeviceId, string EntityId, string Resolution,
    DateTime FromUtc, DateTime ToUtc, string EnergyMethod, List<UsageSeriesDto> Series, List<UsageBucketDto> Buckets);
public sealed record DeviceStateEventDto(DateTime ObservedAtUtc, string State, bool StartsObservationSegment);
