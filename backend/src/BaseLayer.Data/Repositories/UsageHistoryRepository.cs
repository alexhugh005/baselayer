using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
namespace BaseLayer.Data.Repositories;

public sealed class UsageHistoryRepository(PlatformDbContext db, DatabaseGate gate) : IUsageHistoryRepository
{
    public async Task TransactionAsync(Func<Task> operation)
    {
        await gate.Semaphore.WaitAsync();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await operation();
            await transaction.CommitAsync();
        }
        finally { gate.Semaphore.Release(); }
    }
    public Task<Home?> RecordingHomeAsync(Guid homeId) => db.Homes.AsNoTracking().Include(h => h.Devices).SingleOrDefaultAsync(h => h.Id == homeId);
    public Task<Device?> OwnedDeviceAsync(string ownerId, Guid homeId, string entityId) => db.Set<Device>().AsNoTracking()
        .Where(d => d.HomeId == homeId && d.EntityId == entityId && db.Homes.Any(h => h.Id == homeId && h.OwnerId == ownerId)).SingleOrDefaultAsync();
    public Task<List<DeviceMeasurementSeries>> OpenSeriesAsync(Guid homeId) => db.Set<DeviceMeasurementSeries>().Where(s => s.HomeId == homeId && s.EndedUtc == null).ToListAsync();
    public Task<List<DeviceUsageCursor>> CursorsAsync(Guid homeId) => db.Set<DeviceUsageCursor>().Where(c => c.HomeId == homeId).ToListAsync();
    public Task<List<DeviceUsageBucket>> RecentMinutesAsync(Guid homeId, DateTime fromUtc) => db.Set<DeviceUsageBucket>()
        .Where(b => b.Resolution == UsageResolutions.Minute && b.BucketStartUtc >= fromUtc &&
            db.Set<DeviceMeasurementSeries>().Any(s => s.Id == b.MeasurementSeriesId && s.HomeId == homeId)).ToListAsync();
    public Task<List<DeviceMeasurementSeries>> SeriesAsync(Guid deviceId, DateTime fromUtc, DateTime toUtc) => db.Set<DeviceMeasurementSeries>().AsNoTracking()
        .Where(s => s.DeviceId == deviceId && s.StartedUtc < toUtc && (s.EndedUtc == null || s.EndedUtc > fromUtc)).OrderBy(s => s.StartedUtc).Take(20001).ToListAsync();
    public Task<List<DeviceUsageBucket>> BucketsAsync(Guid deviceId, string resolution, DateTime fromUtc, DateTime toUtc) => db.Set<DeviceUsageBucket>().AsNoTracking()
        .Where(b => b.Resolution == resolution && b.BucketStartUtc >= fromUtc && b.BucketStartUtc < toUtc &&
            db.Set<DeviceMeasurementSeries>().Any(s => s.Id == b.MeasurementSeriesId && s.DeviceId == deviceId))
        .OrderBy(b => b.BucketStartUtc).ThenBy(b => b.MeasurementSeriesId).Take(20001).ToListAsync();
    public Task<List<DeviceStateEvent>> StateEventsAsync(Guid deviceId, DateTime fromUtc, DateTime toUtc, int limit) => db.Set<DeviceStateEvent>().AsNoTracking()
        .Where(e => e.DeviceId == deviceId && e.ObservedAtUtc >= fromUtc && e.ObservedAtUtc < toUtc).OrderBy(e => e.ObservedAtUtc).Take(limit).ToListAsync();
    public async Task<DateTime?> NextPendingHourAsync(DateTime beforeUtc)
    {
        var minute = await db.Set<DeviceUsageBucket>().Where(b => b.Resolution == UsageResolutions.Minute && b.RollupPending && b.BucketStartUtc < beforeUtc)
            .OrderBy(b => b.BucketStartUtc).Select(b => (DateTime?)b.BucketStartUtc).FirstOrDefaultAsync();
        return minute is { } value ? UsageHistoryService.FloorHour(value) : null;
    }
    public Task<List<DeviceUsageBucket>> MinutesInHourAsync(DateTime hourUtc) => db.Set<DeviceUsageBucket>()
        .Where(b => b.Resolution == UsageResolutions.Minute && b.BucketStartUtc >= hourUtc && b.BucketStartUtc < hourUtc.AddHours(1)).ToListAsync();
    public Task<List<DeviceUsageBucket>> HourBucketsAsync(DateTime hourUtc) => db.Set<DeviceUsageBucket>()
        .Where(b => b.Resolution == UsageResolutions.Hour && b.BucketStartUtc == hourUtc).ToListAsync();
    public async Task DeleteExpiredAsync(DateTime minuteCutoffUtc, DateTime hourCutoffUtc)
    {
        // Only remove source minutes whose entire hour has a committed summary.
        await db.Set<DeviceUsageBucket>().Where(b => b.Resolution == UsageResolutions.Minute && !b.RollupPending && b.BucketStartUtc < minuteCutoffUtc).ExecuteDeleteAsync();
        await db.Set<DeviceUsageBucket>().Where(b => b.Resolution == UsageResolutions.Hour && b.BucketStartUtc < hourCutoffUtc).ExecuteDeleteAsync();
        await db.Set<DeviceStateEvent>().Where(e => e.ObservedAtUtc < hourCutoffUtc).ExecuteDeleteAsync();
        await db.Set<DeviceMeasurementSeries>().Where(s => s.EndedUtc < hourCutoffUtc && !db.Set<DeviceUsageBucket>().Any(b => b.MeasurementSeriesId == s.Id)).ExecuteDeleteAsync();
    }
    public void Add(DeviceMeasurementSeries series) => db.Add(series);
    public void Add(DeviceUsageBucket bucket) => db.Add(bucket);
    public void Add(DeviceUsageCursor cursor) => db.Add(cursor);
    public void Add(DeviceStateEvent stateEvent) => db.Add(stateEvent);
    public async Task SaveAsync() => await db.SaveChangesAsync();
}
