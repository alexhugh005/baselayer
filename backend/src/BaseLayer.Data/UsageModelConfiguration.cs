using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
namespace BaseLayer.Data;

internal static class UsageModelConfiguration
{
    public static void Configure(ModelBuilder b)
    {
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var series = b.Entity<DeviceMeasurementSeries>();
        series.ToTable("DeviceMeasurementSeries");
        series.Property(s => s.Id).ValueGeneratedNever();
        series.HasOne<Home>().WithMany().HasForeignKey(s => s.HomeId).OnDelete(DeleteBehavior.Cascade);
        series.HasOne<Device>().WithMany().HasForeignKey(s => s.DeviceId).OnDelete(DeleteBehavior.Cascade);
        series.HasIndex(s => new { s.HomeId, s.DeviceId }).IsUnique().HasFilter("EndedUtc IS NULL");
        series.HasIndex(s => new { s.DeviceId, s.StartedUtc });
        series.Property(s => s.StartedUtc).HasConversion(utc);
        series.Property(s => s.EndedUtc).HasConversion(utc);
        var bucket = b.Entity<DeviceUsageBucket>();
        bucket.ToTable("DeviceUsageBuckets");
        bucket.HasKey(x => new { x.MeasurementSeriesId, x.Resolution, x.BucketStartUtc });
        bucket.HasOne<DeviceMeasurementSeries>().WithMany().HasForeignKey(x => x.MeasurementSeriesId).OnDelete(DeleteBehavior.Cascade);
        bucket.HasIndex(x => new { x.Resolution, x.RollupPending, x.BucketStartUtc });
        bucket.HasIndex(x => new { x.Resolution, x.BucketStartUtc });
        bucket.Property(x => x.BucketStartUtc).HasConversion(utc);
        var cursor = b.Entity<DeviceUsageCursor>();
        cursor.ToTable("DeviceUsageCursors");
        cursor.HasKey(x => x.DeviceId);
        cursor.Property(x => x.DeviceId).ValueGeneratedNever();
        cursor.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        cursor.HasOne<Home>().WithMany().HasForeignKey(x => x.HomeId).OnDelete(DeleteBehavior.Cascade);
        cursor.Property(x => x.LastObservedUtc).HasConversion(utc);
        var state = b.Entity<DeviceStateEvent>();
        state.ToTable("DeviceStateEvents");
        state.Property(x => x.Id).ValueGeneratedNever();
        state.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        state.HasIndex(x => new { x.DeviceId, x.ObservedAtUtc }).IsUnique();
        state.HasIndex(x => x.ObservedAtUtc);
        state.Property(x => x.ObservedAtUtc).HasConversion(utc);
    }
}
