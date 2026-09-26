using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
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
    private async Task<ProviderTokens> TokenAsync(string origin, Dictionary<string, string> values, string? refresh = null)
    {
        values["client_id"] = ClientId;
        using var response = await http.PostAsync(ValidateOrigin(origin) + "/auth/token", new FormUrlEncodedContent(values));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return new(root.GetProperty("access_token").GetString()!, root.TryGetProperty("refresh_token", out var token) ? token.GetString()! : refresh!, DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()));
    }
    public Task<ProviderTokens> ExchangeAsync(string origin, string code) => TokenAsync(origin, new() { ["grant_type"] = "authorization_code", ["code"] = code });
    public Task<ProviderTokens> RefreshAsync(string origin, ProviderTokens tokens) => TokenAsync(origin, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken }, tokens.RefreshToken);
    private async Task<JsonDocument> ApiAsync(string origin, ProviderTokens tokens, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, ValidateOrigin(origin) + path);
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
        foreach (var e in document.RootElement.EnumerateArray())
        {
            var id = e.GetProperty("entity_id").GetString()!;
            var state = e.GetProperty("state").GetString()!;
            var attrs = e.GetProperty("attributes");
            var name = attrs.TryGetProperty("friendly_name", out var n) ? n.GetString() ?? id : id;
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
        return new(devices, sensors, power);
    }
    public async Task TurnOffAsync(string origin, ProviderTokens tokens, string entityId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(entityId, @"^(switch|light|fan|climate)\.[a-z0-9_]+$"))
            throw new ArgumentException("Unsupported device.");
        using var result = await ApiAsync(origin, tokens, HttpMethod.Post, $"/api/services/{entityId.Split('.')[0]}/turn_off", new
        {
            entity_id = entityId
        });
    }
    public async Task RevokeAsync(string origin, ProviderTokens tokens)
    {
        using var response = await http.PostAsync(ValidateOrigin(origin) + "/auth/token", new FormUrlEncodedContent(new Dictionary<string, string> { { "action", "revoke" }, { "token", tokens.RefreshToken } }));
        response.EnsureSuccessStatusCode();
    }
}
