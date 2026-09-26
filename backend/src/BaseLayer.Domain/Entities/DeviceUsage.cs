namespace BaseLayer.Domain.Entities;

public static class UsageResolutions
{
    public const string Minute = "minute";
    public const string Hour = "hour";
}

public sealed class DeviceMeasurementSeries
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HomeId { get; set; }
    public Guid DeviceId { get; set; }
    public string PowerSensorEntityId { get; set; } = "";
    public int MappingRevision { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
}

public sealed class DeviceUsageBucket
{
    public Guid MeasurementSeriesId { get; set; }
    public DateTime BucketStartUtc { get; set; }
    public string Resolution { get; set; } = UsageResolutions.Minute;
    public double WattSeconds { get; set; }
    public double CoveredSeconds { get; set; }
    public double? MinWatts { get; set; }
    public double? MaxWatts { get; set; }
    public int SampleCount { get; set; }
    public bool RollupPending { get; set; }
}

public sealed class DeviceStateEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public string State { get; set; } = "unknown";
    public bool StartsObservationSegment { get; set; }
}

// Internal checkpoint, committed atomically with buckets and state events.
public sealed class DeviceUsageCursor
{
    public Guid DeviceId { get; set; }
    public Guid HomeId { get; set; }
    public Guid? MeasurementSeriesId { get; set; }
    public Guid SessionId { get; set; }
    public DateTime LastObservedUtc { get; set; }
    public double? LastPowerWatts { get; set; }
    public string LastState { get; set; } = "unknown";
}
