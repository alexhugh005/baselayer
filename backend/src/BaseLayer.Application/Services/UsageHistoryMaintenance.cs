using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;
namespace BaseLayer.Application.Services;

public sealed class UsageHistoryMaintenance(IUsageHistoryRepository repository, TimeProvider clock)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Leave time for the final polling interval to cross the hour boundary.
        var completedBefore = UsageHistoryService.FloorHour(now - UsageHistoryService.MaximumGap);
        for (var i = 0; i < 24 && !cancellationToken.IsCancellationRequested; i++)
        {
            var found = false;
            await repository.TransactionAsync(async () =>
            {
                var next = await repository.NextPendingHourAsync(completedBefore);
                if (next is not { } hour) return;
                found = true;
                var minutes = await repository.MinutesInHourAsync(hour);
                var existing = (await repository.HourBucketsAsync(hour)).ToDictionary(b => b.MeasurementSeriesId);
                foreach (var group in minutes.GroupBy(b => b.MeasurementSeriesId))
                {
                    if (!existing.TryGetValue(group.Key, out var bucket))
                    {
                        bucket = new() { MeasurementSeriesId = group.Key, BucketStartUtc = hour, Resolution = UsageResolutions.Hour };
                        repository.Add(bucket);
                    }
                    // Replace totals, rather than incrementing, so retrying is idempotent.
                    bucket.WattSeconds = group.Sum(b => b.WattSeconds);
                    bucket.CoveredSeconds = group.Sum(b => b.CoveredSeconds);
                    bucket.SampleCount = group.Sum(b => b.SampleCount);
                    bucket.MinWatts = group.Min(b => b.MinWatts);
                    bucket.MaxWatts = group.Max(b => b.MaxWatts);
                    foreach (var minute in group) minute.RollupPending = false;
                }
                await repository.SaveAsync();
            });
            if (!found) break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await repository.TransactionAsync(() => repository.DeleteExpiredAsync(
            UsageHistoryService.FloorHour(now.AddDays(-90)), UsageHistoryService.FloorHour(now.AddYears(-2))));
    }
}
