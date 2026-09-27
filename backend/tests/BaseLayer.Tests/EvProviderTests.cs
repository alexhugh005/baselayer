using System.Net;
using System.Text;
using System.Text.Json;
using BaseLayer.Api.Providers;
using BaseLayer.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BaseLayer.Tests;

public sealed class EvProviderTests
{
    [Fact]
    public async Task RestoresClimateModeUsingTheRecordedModeInsteadOfGenericTurnOn()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:AllowedOrigins:0"] = "http://localhost:8123" }).Build();
        var provider = new HomeAssistantProvider(http, config, new Environment());
        var tokens = new ProviderTokens("test", "test", DateTime.UtcNow.AddHours(1));
        await provider.RestoreDeviceStateAsync("http://localhost:8123", tokens, "climate.room", "cool");
        Assert.Equal("/api/services/climate/set_hvac_mode", handler.Path);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("cool", payload.RootElement.GetProperty("hvac_mode").GetString());
        await provider.RestoreDeviceStateAsync("http://localhost:8123", tokens, "light.lamp", "on");
        Assert.Equal("/api/services/light/turn_on", handler.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RestoreDeviceStateAsync("http://localhost:8123", tokens, "climate.room", "unknown"));
    }
    [Fact]
    public async Task DiscoversNativeSpanPriorityOptionsAndSendsSelectService()
    {
        var handler = new Handler { Response = """
            [{"entity_id":"select.span_panel_living_room_circuit_priority","state":"off_grid","attributes":{"options":["never","soc_threshold","off_grid","unknown"]}},
             {"entity_id":"select.span_panel_offline_circuit_priority","state":"unavailable","attributes":{"options":["never","off_grid"]}},
             {"entity_id":"select.unrelated","state":"never","attributes":{"options":["never","off_grid"]}}]
            """ };
        using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:AllowedOrigins:0"] = "http://localhost:8123" }).Build();
        var provider = new HomeAssistantProvider(http, config, new Environment());
        var tokens = new ProviderTokens("test", "test", DateTime.UtcNow.AddHours(1));
        var snapshot = await provider.ReadAsync("http://localhost:8123", tokens);
        Assert.Equal(2, snapshot.CircuitPriorities!.Count);
        Assert.Equal("off_grid", snapshot.CircuitPriorities[0].Priority);
        Assert.Equal(new[] { "never", "soc_threshold", "off_grid" }, snapshot.CircuitPriorities[0].Options);
        Assert.Null(snapshot.CircuitPriorities[1].Priority);
        handler.Response = "[]";
        await provider.SetCircuitPriorityAsync("http://localhost:8123", tokens, snapshot.CircuitPriorities[0].EntityId, "never");
        Assert.Equal("/api/services/select/select_option", handler.Path);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(snapshot.CircuitPriorities[0].EntityId, payload.RootElement.GetProperty("entity_id").GetString());
        Assert.Equal("never", payload.RootElement.GetProperty("option").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetCircuitPriorityAsync("http://localhost:8123", tokens, "select.unrelated", "never"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetCircuitPriorityAsync("http://localhost:8123", tokens, snapshot.CircuitPriorities[0].EntityId, "invalid"));
    }
    [Fact]
    public async Task DiscoversBatteryPercentagesAndDoesNotTreatUnknownAsZero()
    {
        var handler = new Handler { Response = """
            [{"entity_id":"sensor.car_battery","state":"42.5","attributes":{"device_class":"battery","unit_of_measurement":"%"}},
             {"entity_id":"sensor.offline","state":"unavailable","attributes":{"device_class":"battery","unit_of_measurement":"%"}},
             {"entity_id":"sensor.invalid","state":"101","attributes":{"device_class":"battery","unit_of_measurement":"%"}},
             {"entity_id":"sensor.humidity","state":"50","attributes":{"device_class":"humidity","unit_of_measurement":"%"}},
             {"entity_id":"sensor.voltage","state":"12","attributes":{"device_class":"battery","unit_of_measurement":"V"}}]
            """ };
        using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:AllowedOrigins:0"] = "http://localhost:8123" }).Build();
        var provider = new HomeAssistantProvider(http, config, new Environment());
        var snapshot = await provider.ReadAsync("http://localhost:8123", new("test", "test", DateTime.UtcNow.AddHours(1)));
        Assert.Equal(3, snapshot.EvBatterySensors!.Count);
        Assert.Equal(42.5, snapshot.EvBatterySensors[0].Percent);
        Assert.Null(snapshot.EvBatterySensors[1].Percent);
        Assert.Null(snapshot.EvBatterySensors[2].Percent);
    }
    [Theory]
    [InlineData("kWh", "75", 75d)]
    [InlineData("Wh", "75000", 75d)]
    [InlineData("kWh", "unavailable", null)]
    [InlineData("kWh", "0", null)]
    public async Task DiscoversMatchingEvCapacityWithoutUsingStoredEnergy(string unit, string state, double? expected)
    {
        var handler = new Handler { Response = $$$"""
            [{"entity_id":"sensor.virtual_ev_battery_charge","state":"42","attributes":{"friendly_name":"Virtual EV Battery Charge","device_class":"battery","unit_of_measurement":"%"}},
             {"entity_id":"input_number.lab_ev_capacity","state":"{{{state}}}","attributes":{"friendly_name":"EV usable battery capacity","unit_of_measurement":"{{{unit}}}"}},
             {"entity_id":"sensor.home_battery_capacity","state":"13.5","attributes":{"unit_of_measurement":"kWh"}},
             {"entity_id":"sensor.virtual_ev_energy","state":"8","attributes":{"unit_of_measurement":"kWh"}}]
            """ };
        using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:AllowedOrigins:0"] = "http://localhost:8123" }).Build();
        var provider = new HomeAssistantProvider(http, config, new Environment());
        var snapshot = await provider.ReadAsync("http://localhost:8123", new("test", "test", DateTime.UtcNow.AddHours(1)));
        Assert.Equal(expected, Assert.Single(snapshot.EvBatterySensors!).CapacityKwh);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("input_number")]
    public async Task DiscoversMatchingChargeTargetAndSendsItsValue(string domain)
    {
        var handler = new Handler { Response = $$$"""
            [{"entity_id":"sensor.virtual_ev_battery_charge","state":"42","attributes":{"device_class":"battery","unit_of_measurement":"%"}},
             {"entity_id":"{{{domain}}}.lab_ev_target","state":"80","attributes":{"unit_of_measurement":"%","min":50,"max":100,"step":1}},
             {"entity_id":"input_number.lab_ev_soc","state":"42","attributes":{"unit_of_measurement":"%","min":0,"max":100,"step":1}},
             {"entity_id":"number.home_battery_target","state":"90","attributes":{"unit_of_measurement":"%","min":50,"max":100,"step":1}}]
            """ };
        using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:AllowedOrigins:0"] = "http://localhost:8123" }).Build();
        var provider = new HomeAssistantProvider(http, config, new Environment());
        var tokens = new ProviderTokens("test", "test", DateTime.UtcNow.AddHours(1));
        var snapshot = await provider.ReadAsync("http://localhost:8123", tokens);
        var target = Assert.Single(snapshot.EvBatterySensors!).ChargeLimitControl!;
        Assert.Equal(domain + ".lab_ev_target", target.EntityId);
        Assert.Equal(80, target.Percent);
        Assert.Equal(50, target.Min);
        Assert.Equal(100, target.Max);
        Assert.Equal(1, target.Step);
        handler.Response = "[]";
        await provider.SetChargeLimitAsync("http://localhost:8123", tokens, target.EntityId, 85);
        Assert.Equal($"/api/services/{domain}/set_value", handler.Path);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(target.EntityId, payload.RootElement.GetProperty("entity_id").GetString());
        Assert.Equal(85, payload.RootElement.GetProperty("value").GetDouble());
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetChargeLimitAsync("http://localhost:8123", tokens, target.EntityId, 101));
    }

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string Response = "[]";
        public string? Path, Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }
    [Theory]
    [InlineData("number")]
    [InlineData("input_number")]
    public async Task DiscoversAmpControlsAndSendsCorrectServicePayload(string domain)
    {
        var handler = new Handler { Response = $$$"""
            [{"entity_id":"{{{domain}}}.ev","state":"32","attributes":{"friendly_name":"EV current","unit_of_measurement":"A","min":6,"max":48,"step":1}},
             {"entity_id":"number.offline","state":"unavailable","attributes":{"unit_of_measurement":"A","min":6,"max":48,"step":1}},
             {"entity_id":"number.temperature","state":"20","attributes":{"unit_of_measurement":"°C","min":6,"max":48,"step":1}}]
            """ };
        using var http = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:AllowedOrigins:0"] = "http://localhost:8123" }).Build();
        var provider = new HomeAssistantProvider(http, config, new Environment());
        var tokens = new ProviderTokens("test", "test", DateTime.UtcNow.AddHours(1));
        var snapshot = await provider.ReadAsync("http://localhost:8123", tokens);
        Assert.Equal(2, snapshot.CurrentControls!.Count);
        Assert.Equal(32, snapshot.CurrentControls[0].Amps);
        Assert.Null(snapshot.CurrentControls[1].Amps);
        handler.Response = "[]";
        await provider.SetCurrentAsync("http://localhost:8123", tokens, domain + ".ev", 20);
        Assert.Equal($"/api/services/{domain}/set_value", handler.Path);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(domain + ".ev", payload.RootElement.GetProperty("entity_id").GetString());
        Assert.Equal(20, payload.RootElement.GetProperty("value").GetDouble());
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetCurrentAsync("http://localhost:8123", tokens, "switch.ev", 20));
    }
}
