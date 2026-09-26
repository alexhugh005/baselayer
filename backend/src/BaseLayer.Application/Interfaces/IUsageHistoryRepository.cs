using BaseLayer.Domain.Entities;
namespace BaseLayer.Application.Interfaces;

public interface IUsageHistoryRepository
{
    Task TransactionAsync(Func<Task> operation);
    Task<Home?> RecordingHomeAsync(Guid homeId);
    Task<Device?> OwnedDeviceAsync(string ownerId, Guid homeId, string entityId);
    Task<List<DeviceMeasurementSeries>> OpenSeriesAsync(Guid homeId);
    Task<List<DeviceUsageCursor>> CursorsAsync(Guid homeId);
    Task<List<DeviceUsageBucket>> RecentMinutesAsync(Guid homeId, DateTime fromUtc);
    Task<List<DeviceMeasurementSeries>> SeriesAsync(Guid deviceId, DateTime fromUtc, DateTime toUtc);
    Task<List<DeviceUsageBucket>> BucketsAsync(Guid deviceId, string resolution, DateTime fromUtc, DateTime toUtc);
    Task<List<DeviceStateEvent>> StateEventsAsync(Guid deviceId, DateTime fromUtc, DateTime toUtc, int limit);
    Task<DateTime?> NextPendingHourAsync(DateTime beforeUtc);
    Task<List<DeviceUsageBucket>> MinutesInHourAsync(DateTime hourUtc);
    Task<List<DeviceUsageBucket>> HourBucketsAsync(DateTime hourUtc);
    Task DeleteExpiredAsync(DateTime minuteCutoffUtc, DateTime hourCutoffUtc);
    void Add(DeviceMeasurementSeries series);
    void Add(DeviceUsageBucket bucket);
    void Add(DeviceUsageCursor cursor);
    void Add(DeviceStateEvent stateEvent);
    Task SaveAsync();
}
