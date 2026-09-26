using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BaseLayer.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(PlatformDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        // Existing local installations predate migrations. Upgrade their schema in place,
        // preserving OAuth credentials, mappings, permissions, and command history.
        await using var transaction = await db.Database.BeginTransactionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "PRAGMA table_info('Homes')";
        var homeColumns = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                homeColumns.Add(reader.GetString(1));
        if (!homeColumns.Contains("CurrentControlsJson"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes ADD COLUMN CurrentControlsJson TEXT NOT NULL DEFAULT '[]'");
        if (!homeColumns.Contains("CircuitPrioritiesJson"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes ADD COLUMN CircuitPrioritiesJson TEXT NOT NULL DEFAULT '[]'");
        if (!homeColumns.Contains("SmartPowerOffEnabled"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes ADD COLUMN SmartPowerOffEnabled INTEGER NOT NULL DEFAULT 0");
        if (!homeColumns.Contains("SmartPowerOffEventId"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes ADD COLUMN SmartPowerOffEventId TEXT NULL");
        if (!homeColumns.Contains("LastRestoreUtc"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes ADD COLUMN LastRestoreUtc TEXT NULL");
        if (!homeColumns.Contains("PowerSource"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes ADD COLUMN PowerSource TEXT NOT NULL DEFAULT 'wholeHouseMeter'");
        command.CommandText = "PRAGMA table_info('Device')";
        var columns = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        if (!columns.Contains("EvCurrentEntityId"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN EvCurrentEntityId TEXT NULL");
        if (!columns.Contains("EvWattsPerAmp"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN EvWattsPerAmp REAL NOT NULL DEFAULT 240");
        if (!columns.Contains("PowerSensorRevision"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN PowerSensorRevision INTEGER NOT NULL DEFAULT 0");
        if (!columns.Contains("LastOnWatts"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN LastOnWatts REAL NULL");
        if (!columns.Contains("SmartUsageHeld"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN SmartUsageHeld INTEGER NOT NULL DEFAULT 0");
        if (!columns.Contains("ThermostatMinF"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN ThermostatMinF REAL NOT NULL DEFAULT 66");
        if (!columns.Contains("ThermostatMaxF"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN ThermostatMaxF REAL NOT NULL DEFAULT 80");
        if (!columns.Contains("ShutoffLevel"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN ShutoffLevel TEXT NOT NULL DEFAULT 'Sometimes'");
            await db.Database.ExecuteSqlRawAsync("UPDATE Device SET ShutoffLevel = 'Never' WHERE Allowed = 0");
        }
        await RestoreQueueSchema.InitializeAsync(db, columns);
        command.CommandText = "PRAGMA table_info('DeviceCommand')";
        var commandColumns = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                commandColumns.Add(reader.GetString(1));
        if (!commandColumns.Contains("PreviousCurrentAmps"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN PreviousCurrentAmps REAL NULL");
        if (!commandColumns.Contains("IsRestoration"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN IsRestoration INTEGER NOT NULL DEFAULT 0");
        if (!commandColumns.Contains("CurrentControlEntityId"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN CurrentControlEntityId TEXT NULL");
        if (!commandColumns.Contains("PriorityControlEntityId"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN PriorityControlEntityId TEXT NULL");
        if (!commandColumns.Contains("CircuitPriority"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN CircuitPriority TEXT NULL");
        if (!commandColumns.Contains("CurrentAmps"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN CurrentAmps REAL NULL");
        if (!commandColumns.Contains("ManualCircuit"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN ManualCircuit INTEGER NOT NULL DEFAULT 0");
        if (!commandColumns.Contains("UsageBudgetWatts"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN UsageBudgetWatts REAL NULL");
        if (!commandColumns.Contains("UsageRevision"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN UsageRevision TEXT NULL");
        if (!commandColumns.Contains("Action"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN Action TEXT NOT NULL DEFAULT 'Off'");
        if (!commandColumns.Contains("EstimatedWatts"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN EstimatedWatts REAL NULL");
        if (!commandColumns.Contains("Automatic"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE DeviceCommand ADD COLUMN Automatic INTEGER NOT NULL DEFAULT 0");
        await UsageSchema.InitializeAsync(db);
        await transaction.CommitAsync();
    }
}
