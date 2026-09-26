using Microsoft.EntityFrameworkCore;

namespace BaseLayer.Data;

internal static class RestoreQueueSchema
{
    // Runs inside DatabaseInitializer's transaction: copy state before removing legacy columns.
    public static async Task InitializeAsync(PlatformDbContext db, HashSet<string> deviceColumns)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS RestoreQueueEntries (
                DeviceId TEXT NOT NULL CONSTRAINT PK_RestoreQueueEntries PRIMARY KEY,
                QueuedUtc TEXT NOT NULL,
                EstimatedWatts REAL NULL,
                TargetCurrentAmps REAL NULL,
                LastManagedCurrentAmps REAL NULL,
                AtFront INTEGER NOT NULL,
                PowerOn INTEGER NOT NULL,
                EligibleSinceUtc TEXT NULL,
                Status TEXT NOT NULL,
                CONSTRAINT FK_RestoreQueueEntries_Device_DeviceId
                    FOREIGN KEY (DeviceId) REFERENCES Device (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_RestoreQueueEntries_AtFront_QueuedUtc
                ON RestoreQueueEntries (AtFront, QueuedUtc);
            """);

        // Older installations may predate EV restoration or the entire restore feature.
        var fields = new (string Legacy, string Column, string Fallback)[]
        {
            ("RestoreQueuedUtc", "QueuedUtc", "NULL"),
            ("RestoreWatts", "EstimatedWatts", "NULL"),
            ("RestoreCurrentAmps", "TargetCurrentAmps", "NULL"),
            ("LastManagedCurrentAmps", "LastManagedCurrentAmps", "NULL"),
            ("RestoreAtFront", "AtFront", "0"),
            ("RestorePowerOn", "PowerOn", "1"),
            ("RestoreEligibleSinceUtc", "EligibleSinceUtc", "NULL"),
            ("RestoreStatus", "Status", "'waiting'")
        };
        if (deviceColumns.Contains("RestoreQueuedUtc"))
        {
            // Identifiers and defaults come only from the fixed list above, never user input.
            var targets = string.Join(", ", fields.Select(f => f.Column));
            var values = string.Join(", ", fields.Select(f => deviceColumns.Contains(f.Legacy) ? f.Legacy : f.Fallback));
            var copy = $"INSERT INTO RestoreQueueEntries (DeviceId, {targets}) SELECT Id, {values} FROM Device WHERE RestoreQueuedUtc IS NOT NULL";
            await db.Database.ExecuteSqlRawAsync(copy);
        }
        foreach (var field in fields.Where(f => deviceColumns.Contains(f.Legacy)))
        {
            var drop = $"ALTER TABLE Device DROP COLUMN {field.Legacy}";
            await db.Database.ExecuteSqlRawAsync(drop);
        }
    }
}
