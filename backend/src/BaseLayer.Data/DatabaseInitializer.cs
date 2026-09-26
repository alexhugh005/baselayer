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
        var hasPowerSource = false;
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                hasPowerSource |= reader.GetString(1) == "PowerSource";
        if (!hasPowerSource)
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes ADD COLUMN PowerSource TEXT NOT NULL DEFAULT 'wholeHouseMeter'");
        command.CommandText = "PRAGMA table_info('Device')";
        var columns = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        if (!columns.Contains("ThermostatMinF"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN ThermostatMinF REAL NOT NULL DEFAULT 66");
        if (!columns.Contains("ThermostatMaxF"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device ADD COLUMN ThermostatMaxF REAL NOT NULL DEFAULT 80");
        await transaction.CommitAsync();
    }
}
