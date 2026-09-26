using Microsoft.EntityFrameworkCore;
namespace BaseLayer.Data;

internal static class UsageSchema
{
    // Explicit upgrade for installations originally created with EnsureCreated.
    // Executed inside DatabaseInitializer's transaction; never recreates existing data.
    public static Task InitializeAsync(PlatformDbContext db) => db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS DeviceMeasurementSeries (
            Id TEXT NOT NULL PRIMARY KEY,
            HomeId TEXT NOT NULL REFERENCES Homes(Id) ON DELETE CASCADE,
            DeviceId TEXT NOT NULL REFERENCES Device(Id) ON DELETE CASCADE,
            PowerSensorEntityId TEXT NOT NULL,
            MappingRevision INTEGER NOT NULL,
            StartedUtc TEXT NOT NULL,
            EndedUtc TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_DeviceMeasurementSeries_HomeId_DeviceId
            ON DeviceMeasurementSeries(HomeId, DeviceId) WHERE EndedUtc IS NULL;
        CREATE INDEX IF NOT EXISTS IX_DeviceMeasurementSeries_DeviceId_StartedUtc
            ON DeviceMeasurementSeries(DeviceId, StartedUtc);
        CREATE TABLE IF NOT EXISTS DeviceUsageBuckets (
            MeasurementSeriesId TEXT NOT NULL REFERENCES DeviceMeasurementSeries(Id) ON DELETE CASCADE,
            BucketStartUtc TEXT NOT NULL,
            Resolution TEXT NOT NULL,
            WattSeconds REAL NOT NULL,
            CoveredSeconds REAL NOT NULL,
            MinWatts REAL NULL,
            MaxWatts REAL NULL,
            SampleCount INTEGER NOT NULL,
            RollupPending INTEGER NOT NULL,
            PRIMARY KEY(MeasurementSeriesId, Resolution, BucketStartUtc)
        );
        CREATE INDEX IF NOT EXISTS IX_DeviceUsageBuckets_Resolution_RollupPending_BucketStartUtc
            ON DeviceUsageBuckets(Resolution, RollupPending, BucketStartUtc);
        CREATE INDEX IF NOT EXISTS IX_DeviceUsageBuckets_Resolution_BucketStartUtc
            ON DeviceUsageBuckets(Resolution, BucketStartUtc);
        CREATE TABLE IF NOT EXISTS DeviceUsageCursors (
            DeviceId TEXT NOT NULL PRIMARY KEY REFERENCES Device(Id) ON DELETE CASCADE,
            HomeId TEXT NOT NULL REFERENCES Homes(Id) ON DELETE CASCADE,
            MeasurementSeriesId TEXT NULL,
            SessionId TEXT NOT NULL,
            LastObservedUtc TEXT NOT NULL,
            LastPowerWatts REAL NULL,
            LastState TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_DeviceUsageCursors_HomeId ON DeviceUsageCursors(HomeId);
        CREATE TABLE IF NOT EXISTS DeviceStateEvents (
            Id TEXT NOT NULL PRIMARY KEY,
            DeviceId TEXT NOT NULL REFERENCES Device(Id) ON DELETE CASCADE,
            ObservedAtUtc TEXT NOT NULL,
            State TEXT NOT NULL,
            StartsObservationSegment INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_DeviceStateEvents_DeviceId_ObservedAtUtc
            ON DeviceStateEvents(DeviceId, ObservedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_DeviceStateEvents_ObservedAtUtc ON DeviceStateEvents(ObservedAtUtc);
        """);
}
