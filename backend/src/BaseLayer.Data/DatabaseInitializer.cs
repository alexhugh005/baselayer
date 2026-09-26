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
        await transaction.CommitAsync();
    }
}
