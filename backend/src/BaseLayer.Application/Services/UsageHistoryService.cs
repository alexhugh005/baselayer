using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;
namespace BaseLayer.Application.Services;

// A new process always starts a new observation segment, even after a short restart.
public sealed class UsageRecordingSession { public Guid Id { get; } = Guid.NewGuid(); }

public sealed class UsageHistoryService(IUsageHistoryRepository repository, HomeOperationGate operations,
    TimeProvider clock, UsageRecordingSession session)
{
    public static readonly TimeSpan MaximumGap = TimeSpan.FromSeconds(20);
    public static DateTime FloorMinute(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    public static DateTime FloorHour(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
    private static bool Valid(double? value) => value is { } watts && double.IsFinite(watts) && watts >= 0;

    public Task RecordAsync(Guid homeId) => operations.RunAsync(homeId, () => repository.TransactionAsync(async () =>
    {
        var home = await repository.RecordingHomeAsync(homeId);
        var now = clock.GetUtcNow().UtcDateTime;
        if (home is null || home.Revoked || home.LastSeenUtc is not { } observed || observed > now || now - observed > MaximumGap)
            return;
        var series = (await repository.OpenSeriesAsync(homeId)).ToDictionary(s => s.DeviceId);
        var cursors = (await repository.CursorsAsync(homeId)).ToDictionary(c => c.DeviceId);
        var buckets = (await repository.RecentMinutesAsync(homeId, FloorMinute(observed - MaximumGap)))
            .ToDictionary(b => (b.MeasurementSeriesId, b.BucketStartUtc));
        foreach (var device in home.Devices)
        {
            cursors.TryGetValue(device.Id, out var cursor);
            // Replaying the same snapshot must not add observations or energy twice.
            if (cursor is not null && observed <= cursor.LastObservedUtc) continue;
            series.TryGetValue(device.Id, out var active);
            if (active is not null && (active.PowerSensorEntityId != device.PowerSensorId || active.MappingRevision != device.PowerSensorRevision))
            {
                active.EndedUtc = observed;
                // Free the unique open-series key before inserting its replacement.
                await repository.SaveAsync();
                active = null;
            }
            if (active is null && device.PowerSensorId is { } sensor)
            {
                active = new() { HomeId = homeId, DeviceId = device.Id, PowerSensorEntityId = sensor,
                    MappingRevision = device.PowerSensorRevision, StartedUtc = observed };
                repository.Add(active);
            }
            var state = device.Present ? device.State : "unavailable";
            var watts = device.Present && Valid(device.PowerWatts) ? device.PowerWatts : null;
            var continuous = cursor is not null && cursor.SessionId == session.Id && observed - cursor.LastObservedUtc <= MaximumGap;
            if (!continuous || cursor!.LastState != state)
                repository.Add(new DeviceStateEvent { DeviceId = device.Id, ObservedAtUtc = observed,
                    State = state, StartsObservationSegment = !continuous });
            if (active is not null)
            {
                if (continuous && cursor!.MeasurementSeriesId == active.Id && Valid(cursor.LastPowerWatts) && Valid(watts))
                {
                    // Left rectangle: appliances tend to hold their power until their next change.
                    // Both endpoints must be valid; never fill outages or mapping boundaries.
                    var start = cursor.LastObservedUtc;
                    while (start < observed)
                    {
                        var minute = FloorMinute(start);
                        var end = observed < minute.AddMinutes(1) ? observed : minute.AddMinutes(1);
                        var bucket = Bucket(active.Id, minute);
                        var seconds = (end - start).TotalSeconds;
                        bucket.WattSeconds += cursor.LastPowerWatts!.Value * seconds;
                        bucket.CoveredSeconds += seconds;
                        IncludePower(bucket, cursor.LastPowerWatts.Value);
                        bucket.RollupPending = true;
                        start = end;
                    }
                }
                if (Valid(watts))
                {
                    var bucket = Bucket(active.Id, FloorMinute(observed));
                    IncludePower(bucket, watts!.Value);
                    bucket.SampleCount++;
                    bucket.RollupPending = true;
                }
            }
            if (cursor is null)
            {
                cursor = new() { DeviceId = device.Id, HomeId = homeId };
                repository.Add(cursor);
            }
            cursor.MeasurementSeriesId = active?.Id;
            cursor.SessionId = session.Id;
            cursor.LastObservedUtc = observed;
            cursor.LastPowerWatts = watts;
            cursor.LastState = state;
        }
        await repository.SaveAsync();

        DeviceUsageBucket Bucket(Guid seriesId, DateTime minute)
        {
            if (!buckets.TryGetValue((seriesId, minute), out var bucket))
            {
                bucket = new() { MeasurementSeriesId = seriesId, BucketStartUtc = minute };
                buckets.Add((seriesId, minute), bucket);
                repository.Add(bucket);
            }
            return bucket;
        }
    }));

    private static void IncludePower(DeviceUsageBucket bucket, double watts)
    {
        bucket.MinWatts = bucket.MinWatts is { } min ? Math.Min(min, watts) : watts;
        bucket.MaxWatts = bucket.MaxWatts is { } max ? Math.Max(max, watts) : watts;
    }

    public Task BreakObservationAsync(Guid homeId) => operations.RunAsync(homeId, () => repository.TransactionAsync(async () =>
    {
        foreach (var cursor in await repository.CursorsAsync(homeId)) cursor.SessionId = Guid.Empty;
        await repository.SaveAsync();
    }));

    public async Task<DeviceUsageHistoryDto> GetAsync(string ownerId, Guid homeId, string entityId,
        DateTimeOffset from, DateTimeOffset to, string resolution)
    {
        var device = await repository.OwnedDeviceAsync(ownerId, homeId, entityId) ?? throw new KeyNotFoundException("Device not found.");
        var (start, end) = ValidateRange(from, to, resolution);
        var series = await repository.SeriesAsync(device.Id, start, end);
        var buckets = await repository.BucketsAsync(device.Id, resolution, start, end);
        if (buckets.Count > 20000 || series.Count > 20000) throw new ArgumentException("Too much history. Choose a shorter range.");
        var seconds = resolution == UsageResolutions.Minute ? 60d : 3600d;
        return new(homeId, device.Id, entityId, resolution, start, end, "estimatedFromPower",
            series.Select(s => new UsageSeriesDto(s.Id, s.PowerSensorEntityId, s.StartedUtc, s.EndedUtc)).ToList(),
            buckets.Select(b => new UsageBucketDto(b.MeasurementSeriesId, b.BucketStartUtc,
                b.CoveredSeconds > 0 ? b.WattSeconds / b.CoveredSeconds : null,
                b.MinWatts, b.MaxWatts, b.CoveredSeconds > 0 ? b.WattSeconds / 3600 : null,
                b.CoveredSeconds, b.CoveredSeconds / seconds * 100, b.SampleCount)).ToList());
    }

    public async Task<List<DeviceStateEventDto>> GetStateEventsAsync(string ownerId, Guid homeId, string entityId,
        DateTimeOffset from, DateTimeOffset to)
    {
        var device = await repository.OwnedDeviceAsync(ownerId, homeId, entityId) ?? throw new KeyNotFoundException("Device not found.");
        if (from >= to || to - from > TimeSpan.FromDays(7)) throw new ArgumentException("Choose a state history range of at most seven days.");
        var events = await repository.StateEventsAsync(device.Id, from.UtcDateTime, to.UtcDateTime, 10001);
        if (events.Count > 10000) throw new ArgumentException("Too many state events. Choose a shorter range.");
        return events.Select(e => new DeviceStateEventDto(e.ObservedAtUtc, e.State, e.StartsObservationSegment)).ToList();
    }

    private static (DateTime, DateTime) ValidateRange(DateTimeOffset from, DateTimeOffset to, string resolution)
    {
        if (resolution is not (UsageResolutions.Minute or UsageResolutions.Hour)) throw new ArgumentException("Resolution must be minute or hour.");
        var start = from.UtcDateTime;
        var end = to.UtcDateTime;
        var maximum = TimeSpan.FromDays(resolution == UsageResolutions.Minute ? 7 : 93);
        if (start >= end || end - start > maximum) throw new ArgumentException($"Choose a range of at most {maximum.TotalDays:0} days.");
        var ticks = resolution == UsageResolutions.Minute ? TimeSpan.TicksPerMinute : TimeSpan.TicksPerHour;
        if (start.Ticks % ticks != 0 || end.Ticks % ticks != 0) throw new ArgumentException("Range boundaries must align with the requested UTC minute or hour.");
        return (start, end);
    }
}
