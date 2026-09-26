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
