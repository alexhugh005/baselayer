using System.Net;
using System.Text.Json;
using BaseLayer.Api.Providers;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Data;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BaseLayer.Tests;

public sealed class PowerSupplyProviderTests
{
    [Fact]
    public async Task ReadsCircuitEstimatesAndLabReadinessWithoutTreatingStringsAsNumbers()
    {
        using var http = new HttpClient(new Handler("""
            [{"entity_id":"sensor.span_panel_kitchen_power","state":"10","attributes":{"device_class":"power","unit_of_measurement":"W","restoration_estimate_watts":2500}},
             {"entity_id":"sensor.span_panel_office_power","state":"10","attributes":{"device_class":"power","unit_of_measurement":"W","restoration_estimate_watts":"bad"}},
             {"entity_id":"binary_sensor.lab_span_backup_ready","state":"off","attributes":{}}]
            """));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["HomeAssistant:AllowedOrigins:0"] = "http://localhost:8123" }).Build();
        var provider = new HomeAssistantProvider(http, config, new Environment());
        var snapshot = await provider.ReadAsync("http://localhost:8123", new("test", "test", DateTime.UtcNow.AddHours(1)));
        Assert.False(snapshot.BackupReady);
        Assert.Equal(2500, Assert.Single(snapshot.CircuitRestoreEstimates!).Value);
        Assert.Equal("switch.span_panel_kitchen_breaker", Assert.Single(snapshot.CircuitRestoreEstimates!).Key);
    }

    [Theory]
    [InlineData("battery", "battery", true)]
    [InlineData("BATTERY", "battery", true)]
    [InlineData("grid", "grid", false)]
    [InlineData("PV", "solar", false)]
    [InlineData("GENERATOR", "generator", false)]
    [InlineData("NONE", "none", false)]
    [InlineData("unavailable", "unknown", null)]
    [InlineData("unknown", "unknown", null)]
    [InlineData("unexpected", "unknown", null)]
    public async Task ReadsBackupModeIndependentlyOfZeroPower(string input, string state, bool? battery)
    {
        var entities = new[] {
            new { entity_id = "sensor.span_panel_grid_forming_entity", state = input, attributes = new { device_class = "", unit_of_measurement = "" } },
            new { entity_id = "sensor.grid_power", state = "0", attributes = new { device_class = "power", unit_of_measurement = "W" } },
            new { entity_id = "sensor.battery_power", state = "0", attributes = new { device_class = "power", unit_of_measurement = "W" } }
        };
        using var http = new HttpClient(new Handler(JsonSerializer.Serialize(entities)));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["HomeAssistant:AllowedOrigins:0"] = "http://localhost:8123" }).Build();
        var provider = new HomeAssistantProvider(http, config, new Environment());
        var snapshot = await provider.ReadAsync("http://localhost:8123", new("test", "test", DateTime.UtcNow.AddHours(1)));
        Assert.Equal(state, Assert.Single(snapshot.PowerSupplies!).State);
        var now = DateTime.UtcNow;
        var status = PowerSupplyDetection.Current(new Home { LastSeenUtc = now, PowerSupplyJson = JsonSerializer.Serialize(snapshot.PowerSupplies) }, now);
        Assert.Equal(battery, status.IsOnBattery);
        Assert.Equal(state, status.State);
    }

    [Fact]
    public void MissingOrConflictingSourcesAreUnknown()
    {
        var now = DateTime.UtcNow;
        foreach (var readings in new List<PowerSupplyReading>[] {
            [], [new("sensor.a", "battery"), new("sensor.b", "grid")],
            [new("sensor.a", "battery"), new("sensor.b", "unknown")] })
        {
            var status = PowerSupplyDetection.Current(new Home { LastSeenUtc = now, PowerSupplyJson = JsonSerializer.Serialize(readings) }, now);
            Assert.Null(status.IsOnBattery);
            Assert.Equal("unknown", status.State);
        }
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed partial class PlatformTests
{
    [Fact]
    public async Task PowerSupplyTracksBatteryAndGridAcrossPollsAndPersistence()
    {
        provider.Snapshot = new([], [], new(), PowerSupplies: [new("sensor.span_panel_grid_forming_entity", "grid")]);
        var home = await Connect();
        Assert.False(home.PowerSupply!.IsOnBattery);
        provider.Snapshot = provider.Snapshot with { PowerSupplies = [new("sensor.span_panel_grid_forming_entity", "battery")] };
        clock.Advance(3);
        await service.PollAsync(home.Id);
        db.ChangeTracker.Clear();
        var backup = Assert.Single(await service.HomesAsync("alice")).PowerSupply!;
        Assert.True(backup.IsOnBattery);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, backup.ObservedAtUtc);
        Assert.Empty(await service.HomesAsync("bob"));
        provider.Snapshot = provider.Snapshot with { PowerSupplies = [] };
        await service.PollAsync(home.Id);
        Assert.Null(Assert.Single(await service.HomesAsync("alice")).PowerSupply!.IsOnBattery);
        provider.Snapshot = provider.Snapshot with { PowerSupplies = [new("sensor.span_panel_grid_forming_entity", "grid")] };
        await service.PollAsync(home.Id);
        Assert.False(Assert.Single(await service.HomesAsync("alice")).PowerSupply!.IsOnBattery);
    }

    [Fact]
    public async Task StaleAndRevokedHomesDoNotClaimBatteryOrGrid()
    {
        provider.Snapshot = new([], [], new(), PowerSupplies: [new("sensor.span_panel_grid_forming_entity", "battery")]);
        var home = await Connect();
        clock.Advance(21);
        var stale = Assert.Single(await service.HomesAsync("alice")).PowerSupply!;
        Assert.Null(stale.IsOnBattery);
        Assert.Equal("unknown", Assert.Single(stale.Sources).State);
        await service.PollAsync(home.Id);
        Assert.True(Assert.Single(await service.HomesAsync("alice")).PowerSupply!.IsOnBattery);
        await service.RevokeAsync("alice", home.Id);
        Assert.Null(Assert.Single(await service.HomesAsync("alice")).PowerSupply!.IsOnBattery);
    }

    [Fact]
    public async Task PowerSupplySchemaUpgradePreservesExistingHome()
    {
        var home = await Connect();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Homes DROP COLUMN PowerSupplyJson");
        await DatabaseInitializer.InitializeAsync(db);
        await DatabaseInitializer.InitializeAsync(db);
        var upgraded = Assert.Single(await service.HomesAsync("alice"));
        Assert.Equal(home.Id, upgraded.Id);
        Assert.Equal(home.Devices.Count, upgraded.Devices.Count);
        Assert.Null(upgraded.PowerSupply!.IsOnBattery);
    }
}
