using BaseLayer.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    [Fact]
    public async Task CategorySetupRemembersExplicitUnassignedAndPreservesItForOlderClients()
    {
        var home = await Connect();
        Assert.False(Assert.Single(home.Devices).PowerStandardConfigured);
        await Allow(home);
        Assert.False(Assert.Single((await service.HomesAsync("alice"))[0].Devices).PowerStandardConfigured);
        await service.SettingsAsync("alice", home.Id, StandardSettings(null));
        db.ChangeTracker.Clear();
        var saved = Assert.Single((await Allow(home)).Devices);
        Assert.True(saved.PowerStandardConfigured);
        Assert.Null(saved.Category);
        await service.PollAsync(home.Id);
        saved = Assert.Single((await service.HomesAsync("alice"))[0].Devices);
        Assert.True(saved.PowerStandardConfigured);
        Assert.Null(saved.Category);
    }

    [Fact]
    public async Task CategorySetupSchemaUpgradeIsRepeatableAndPreservesSavedStandards()
    {
        var home = await Connect();
        await service.SettingsAsync("alice", home.Id, StandardSettings("custom", 950));
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Device DROP COLUMN PowerStandardConfigured");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        db.ChangeTracker.Clear();
        var saved = Assert.Single((await service.HomesAsync("alice"))[0].Devices);
        Assert.Equal("custom", saved.Category);
        Assert.Equal(950, saved.StandardWattsOverride);
        await service.SettingsAsync("alice", home.Id, StandardSettings(null));
        db.ChangeTracker.Clear();
        Assert.True(Assert.Single((await service.HomesAsync("alice"))[0].Devices).PowerStandardConfigured);
    }
}
