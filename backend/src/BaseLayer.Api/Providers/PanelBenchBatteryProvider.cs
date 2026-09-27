using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
namespace BaseLayer.Api.Providers;

/// <summary>Opt-in local lab adapter; reads the same battery that supplies the HA circuits.</summary>
public sealed class PanelBenchBatteryProvider(HttpClient http, IPlatformRepository repository,
    IConfiguration configuration, TimeProvider clock) : IBatteryProvider
{
    public async Task<BatterySnapshot> ReadAsync(Guid homeId, CancellationToken cancellationToken = default)
    {
        var home = await repository.HomeAsync(homeId);
        var expectedOrigin = configuration["Battery:PanelBench:HomeAssistantOrigin"] ?? "http://localhost:8123";
        if (home is null || home.Revoked || !string.Equals(home.BaseUrl.TrimEnd('/'), expectedOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new BatteryProviderException("This home is not connected to the configured virtual battery lab.");
        var healthUrl = configuration["Battery:PanelBench:HealthUrl"] ?? "http://127.0.0.1:18081/health";
        using var response = await http.GetAsync(healthUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.GetProperty("connected").GetBoolean() || root.GetProperty("battery").ValueKind != JsonValueKind.Object)
                throw new BatteryProviderException("The virtual battery lab is disconnected.");
            var battery = root.GetProperty("battery");
            return new(battery.GetProperty("capacity_kwh").GetDouble(), battery.GetProperty("stored_energy_kwh").GetDouble(), clock.GetUtcNow(), true);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new BatteryProviderException("The virtual battery lab returned invalid telemetry.", ex);
        }
    }
}
