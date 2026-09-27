using Microsoft.EntityFrameworkCore;
namespace BaseLayer.Data;

internal static class AnomalySchema
{
    public static Task<int> InitializeAsync(PlatformDbContext db) => db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS UsageAnomalies (
            Id TEXT NOT NULL PRIMARY KEY, HomeId TEXT NOT NULL, EventId TEXT NOT NULL,
            EntityId TEXT NOT NULL, UsualWatts REAL NOT NULL, ObservedWatts REAL NOT NULL,
            RecommendedAction TEXT NOT NULL, DetectedUtc TEXT NOT NULL, ReceivedUtc TEXT NOT NULL,
            PowerSensorRevision INTEGER NOT NULL, Status TEXT NOT NULL, Message TEXT NOT NULL,
            CommandId TEXT NULL, ConfirmedUtc TEXT NULL, LastSavingsObservationUtc TEXT NULL,
            SavingsClosed INTEGER NOT NULL, AvoidedWatts REAL NOT NULL, EstimatedSavedKwh REAL NOT NULL,
            FOREIGN KEY (HomeId) REFERENCES Homes(Id) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_UsageAnomalies_HomeId_EventId ON UsageAnomalies(HomeId, EventId);
        """);
}
