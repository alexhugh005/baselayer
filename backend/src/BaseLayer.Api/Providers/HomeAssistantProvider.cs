using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
namespace BaseLayer.Api.Providers;

public sealed class CredentialProtector(IDataProtectionProvider provider) : ICredentialProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("BaseLayer.HomeAssistant.OAuth.v1");
    public string Protect(ProviderTokens tokens) => protector.Protect(JsonSerializer.Serialize(tokens));
    public ProviderTokens Unprotect(string value) => JsonSerializer.Deserialize<ProviderTokens>(protector.Unprotect(value))!;
}
public sealed class HomeAssistantProvider(HttpClient http, IConfiguration config, IHostEnvironment environment) : ISmartHomeProvider
{
    private string ClientId => config["HomeAssistant:ClientId"] ?? "http://localhost:5173/";
    private string RedirectUri => config["HomeAssistant:RedirectUri"] ?? "http://localhost:5173/oauth/home-assistant";
    public string ValidateOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter a Home Assistant origin without a path.");
        var normalized = uri.GetLeftPart(UriPartial.Authority);
        var allowed = config.GetSection("HomeAssistant:AllowedOrigins").Get<string[]>() ?? [];
        if (!allowed.Contains(normalized, StringComparer.Ordinal) || (uri.Scheme != "https" && !(environment.IsDevelopment() && uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("This Home Assistant origin is not enabled by the server administrator.");
        return normalized;
    }
    public string AuthorizationUrl(string origin, string state) => $"{ValidateOrigin(origin)}/auth/authorize?response_type=code&client_id={Uri.EscapeDataString(ClientId)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&state={Uri.EscapeDataString(state)}";
    private string TransportOrigin(string origin)
    {
        // The browser uses localhost; containers reach HA by its private DNS name.
        // Always validate the submitted public origin before applying an admin mapping.
        var validated = ValidateOrigin(origin);
        if (validated != config["HomeAssistant:Transport:PublicOrigin"])
            return validated;
        var target = config["HomeAssistant:Transport:InternalOrigin"];
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != "https" && !(environment.IsDevelopment() && uri.Scheme == "http")))
            throw new InvalidOperationException("Configure a valid Home Assistant transport origin; HTTP requires Development.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    private async Task<ProviderTokens> TokenAsync(string origin, Dictionary<string, string> values, string? refresh = null)
    {
        values["client_id"] = ClientId;
        using var response = await http.PostAsync(TransportOrigin(origin) + "/auth/token", new FormUrlEncodedContent(values));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return new(root.GetProperty("access_token").GetString()!, root.TryGetProperty("refresh_token", out var token) ? token.GetString()! : refresh!, DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()));
    }
    public Task<ProviderTokens> ExchangeAsync(string origin, string code) => TokenAsync(origin, new() { ["grant_type"] = "authorization_code", ["code"] = code });
    public Task<ProviderTokens> RefreshAsync(string origin, ProviderTokens tokens) => TokenAsync(origin, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken }, tokens.RefreshToken);
    private async Task<JsonDocument> ApiAsync(string origin, ProviderTokens tokens, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, TransportOrigin(origin) + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    public async Task<ProviderSnapshot> ReadAsync(string origin, ProviderTokens tokens)
    {
        using var document = await ApiAsync(origin, tokens, HttpMethod.Get, "/api/states");
        var devices = new List<ProviderDevice>();
        var sensors = new List<PowerSensorDto>();
        var power = new Dictionary<string, double?>();
        var controls = new List<CurrentControlDto>();
        var evBatteries = new List<EvBatterySensorDto>();
        var chargeLimits = new List<ChargeLimitControlDto>();
        var evCapacities = new List<(string Id, string Name, double? Kwh)>();
        var priorities = new List<CircuitPriorityDto>();
        var supplies = new List<PowerSupplyReading>();
        var restoreEstimates = new Dictionary<string, double>();
        var circuitDevices = new Dictionary<string, List<string>>();
        var circuitSupplies = new Dictionary<string, string>();
        var entityStates = new Dictionary<string, string>();
        bool? backupReady = null;
        foreach (var e in document.RootElement.EnumerateArray())
        {
            var id = e.GetProperty("entity_id").GetString()!;
            var state = e.GetProperty("state").GetString()!;
            entityStates[id] = state;
            var attrs = e.GetProperty("attributes");
            var name = attrs.TryGetProperty("friendly_name", out var n) ? n.GetString() ?? id : id;
            if (id.StartsWith("sensor.span_panel_", StringComparison.Ordinal) && id.EndsWith("_power", StringComparison.Ordinal))
            {
                var circuitId = "switch." + id["sensor.".Length..^"_power".Length] + "_breaker";
                if (attrs.TryGetProperty("restoration_devices", out var members) && members.ValueKind == JsonValueKind.Array)
                    circuitDevices[circuitId] = members.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!).Where(OutageRecoveryPolicy.IsRestorableDevice).Distinct().ToList();
                if (attrs.TryGetProperty("circuit_supply_entity", out var supply) && supply.ValueKind == JsonValueKind.String)
                    circuitSupplies[circuitId] = supply.GetString()!;
            }
            if (id == "binary_sensor.lab_span_backup_ready") backupReady = state == "on";
            if (id.StartsWith("sensor.span_panel_", StringComparison.Ordinal) && id.EndsWith("_power", StringComparison.Ordinal) &&
                attrs.TryGetProperty("restoration_estimate_watts", out var estimate) && estimate.ValueKind == JsonValueKind.Number && estimate.TryGetDouble(out var estimateWatts) &&
                double.IsFinite(estimateWatts) && estimateWatts >= 0)
                restoreEstimates["switch." + id["sensor.".Length..^"_power".Length] + "_breaker"] = estimateWatts;
            // SPAN's grid-forming entity identifies backup mode even at zero load.
            // Power flow alone cannot distinguish an outage from self-consumption.
            if (id.StartsWith("sensor.") && id.EndsWith("_grid_forming_entity", StringComparison.Ordinal))
                supplies.Add(new(id, PowerSupplyDetection.Normalize(state)));
            if (SmartPanelCircuit.IsPriorityControl(id) && attrs.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                var supported = options.EnumerateArray()
                    .Where(o => o.ValueKind == JsonValueKind.String)
                    .Select(o => o.GetString()!).Where(SmartPanelCircuit.ValidPriority).Distinct().ToList();
                priorities.Add(new(id, supported.Contains(state) ? state : null, supported));
            }
            if ((id.StartsWith("number.") || id.StartsWith("input_number.")) &&
                attrs.TryGetProperty("unit_of_measurement", out var currentUnit) && currentUnit.GetString() == "A" &&
                attrs.TryGetProperty("min", out var min) && min.TryGetDouble(out var minimum) &&
                attrs.TryGetProperty("max", out var max) && max.TryGetDouble(out var maximum) &&
                attrs.TryGetProperty("step", out var step) && step.TryGetDouble(out var increment) &&
                double.IsFinite(minimum) && minimum >= 0 && double.IsFinite(maximum) && maximum > minimum &&
                double.IsFinite(increment) && increment > 0)
            {
                double? amps = double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                    double.IsFinite(a) && a >= minimum && a <= maximum ? a : null;
                controls.Add(new(id, name, amps, minimum, maximum, increment));
            }
            if ((id.StartsWith("sensor.") || id.StartsWith("input_number.") || id.StartsWith("number.")) &&
                System.Text.RegularExpressions.Regex.IsMatch(id + " " + name, @"\bcapacity\b|_capacity(?:_|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                attrs.TryGetProperty("unit_of_measurement", out var capacityUnit) && capacityUnit.GetString() is "kWh" or "Wh")
            {
                var parsedCapacity = double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var capacity);
                var kwh = capacity * (capacityUnit.GetString() == "Wh" ? 0.001 : 1);
                evCapacities.Add((id, name, parsedCapacity && double.IsFinite(kwh) && kwh > 0 && kwh <= 1000 ? kwh : null));
            }
            if ((id.StartsWith("number.") || id.StartsWith("input_number.")) &&
                attrs.TryGetProperty("unit_of_measurement", out var targetUnit) && targetUnit.GetString() == "%" &&
                System.Text.RegularExpressions.Regex.IsMatch(id + " " + name, @"(?:^|[ _])(?:target|limit)(?:[ _]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                attrs.TryGetProperty("min", out var targetMin) && targetMin.TryGetDouble(out var minPercent) &&
                attrs.TryGetProperty("max", out var targetMax) && targetMax.TryGetDouble(out var maxPercent) &&
                attrs.TryGetProperty("step", out var targetStep) && targetStep.TryGetDouble(out var percentStep) &&
                double.IsFinite(minPercent) && minPercent >= 0 && double.IsFinite(maxPercent) && maxPercent <= 100 && maxPercent > minPercent &&
                double.IsFinite(percentStep) && percentStep > 0)
            {
                double? percent = double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var targetPercent) &&
                    double.IsFinite(targetPercent) && targetPercent >= minPercent && targetPercent <= maxPercent ? targetPercent : null;
                chargeLimits.Add(new(id, name, percent, minPercent, maxPercent, percentStep));
            }
            // Battery percentage alone does not identify a vehicle; match its identity later.
            if (id.StartsWith("sensor.") && attrs.TryGetProperty("device_class", out var batteryClass) &&
                batteryClass.GetString() == "battery" && attrs.TryGetProperty("unit_of_measurement", out var batteryUnit) && batteryUnit.GetString() == "%")
            {
                double? percent = double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var charge) &&
                    double.IsFinite(charge) && charge >= 0 && charge <= 100 ? charge : null;
                evBatteries.Add(new(id, name, percent));
            }
            if (new[] { "switch", "light", "fan", "climate" }.Contains(id.Split('.')[0]))
                devices.Add(new(id, name, state));
            if (!id.StartsWith("sensor.") || !attrs.TryGetProperty("device_class", out var dc) || dc.GetString() != "power" || !attrs.TryGetProperty("unit_of_measurement", out var unit))
                continue;
            var u = unit.GetString();
            if (u is not ("W" or "kW"))
                continue;
            sensors.Add(new(id, name, u));
            var parsed = double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
            var watts = value * (u == "kW" ? 1000 : 1);
            power[id] = parsed && double.IsFinite(watts) && watts >= 0 ? watts : null;
        }
        evBatteries = evBatteries.Select(battery =>
        {
            var matches = evCapacities.Where(c => EvBatteryDiscovery.Matches(battery.EntityId, battery.Name, c.Id, c.Name)).ToList();
            var capacity = matches.Count == 1 && evBatteries.Count(other => EvBatteryDiscovery.Matches(other.EntityId, other.Name, matches[0].Id, matches[0].Name)) == 1
                ? matches[0].Kwh : null;
            var targets = chargeLimits.Where(c => EvBatteryDiscovery.Matches(battery.EntityId, battery.Name, c.EntityId, c.Name)).ToList();
            var target = targets.Count == 1 && evBatteries.Count(other => EvBatteryDiscovery.Matches(other.EntityId, other.Name, targets[0].EntityId, targets[0].Name)) == 1
                ? targets[0] : null;
            return battery with { CapacityKwh = capacity, ChargeLimitControl = target };
        }).ToList();
        return new(devices, sensors, power, controls, priorities, supplies, restoreEstimates, backupReady, circuitDevices,
            circuitSupplies.ToDictionary(x => x.Key, x => entityStates.GetValueOrDefault(x.Value) switch { "on" => (bool?)true, "off" => false, _ => null }), evBatteries);
    }
    public async Task SetCircuitPriorityAsync(string origin, ProviderTokens tokens, string entityId, string priority)
    {
        if (!SmartPanelCircuit.IsPriorityControl(entityId) || !SmartPanelCircuit.ValidPriority(priority))
            throw new ArgumentException("Unsupported circuit outage setting.");
        using var result = await ApiAsync(origin, tokens, HttpMethod.Post, "/api/services/select/select_option", new { entity_id = entityId, option = priority });
    }
    public async Task SetCurrentAsync(string origin, ProviderTokens tokens, string entityId, double amps)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(entityId, @"^(number|input_number)\.[a-z0-9_]+$") || !double.IsFinite(amps) || amps < 0)
            throw new ArgumentException("Unsupported charging current control.");
        using var result = await ApiAsync(origin, tokens, HttpMethod.Post, $"/api/services/{entityId.Split('.')[0]}/set_value", new { entity_id = entityId, value = amps });
    }
    public Task SetChargeLimitAsync(string origin, ProviderTokens tokens, string entityId, double percent)
    {
        if (!double.IsFinite(percent) || percent < 0 || percent > 100)
            throw new ArgumentException("Invalid charge limit.");
        return SetCurrentAsync(origin, tokens, entityId, percent);
    }
    public Task TurnOffAsync(string origin, ProviderTokens tokens, string entityId) => SetPowerAsync(origin, tokens, entityId, "turn_off");
    public Task TurnOnAsync(string origin, ProviderTokens tokens, string entityId) => SetPowerAsync(origin, tokens, entityId, "turn_on");
    public async Task RestoreDeviceStateAsync(string origin, ProviderTokens tokens, string entityId, string state)
    {
        if (!OutageRecoveryPolicy.IsRestorableDevice(entityId) || !OutageRecoveryPolicy.IsRunningState(entityId, state))
            throw new ArgumentException("Unsupported device restoration state.");
        if (!entityId.StartsWith("climate.", StringComparison.Ordinal))
        {
            await TurnOnAsync(origin, tokens, entityId);
            return;
        }
        using var result = await ApiAsync(origin, tokens, HttpMethod.Post, "/api/services/climate/set_hvac_mode",
            new { entity_id = entityId, hvac_mode = state });
    }
    private async Task SetPowerAsync(string origin, ProviderTokens tokens, string entityId, string action)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(entityId, @"^(switch|light|fan|climate)\.[a-z0-9_]+$"))
            throw new ArgumentException("Unsupported device.");
        using var result = await ApiAsync(origin, tokens, HttpMethod.Post, $"/api/services/{entityId.Split('.')[0]}/{action}", new
        {
            entity_id = entityId
        });
    }
    public async Task RevokeAsync(string origin, ProviderTokens tokens)
    {
        using var response = await http.PostAsync(TransportOrigin(origin) + "/auth/token", new FormUrlEncodedContent(new Dictionary<string, string> { { "action", "revoke" }, { "token", tokens.RefreshToken } }));
        response.EnsureSuccessStatusCode();
    }
}
