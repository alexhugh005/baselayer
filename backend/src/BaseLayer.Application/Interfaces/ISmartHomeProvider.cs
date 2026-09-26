using BaseLayer.Application.Contracts;
namespace BaseLayer.Application.Interfaces;

public sealed record ProviderTokens(string AccessToken, string RefreshToken, DateTime ExpiresUtc);
public sealed record ProviderDevice(string EntityId, string Name, string State);
public sealed record ProviderSnapshot(List<ProviderDevice> Devices, List<PowerSensorDto> Sensors, Dictionary<string, double?> Power);
public interface ISmartHomeProvider
{
    string ValidateOrigin(string origin);
    string AuthorizationUrl(string origin, string state);
    Task<ProviderTokens> ExchangeAsync(string origin, string code);
    Task<ProviderTokens> RefreshAsync(string origin, ProviderTokens tokens);
    Task<ProviderSnapshot> ReadAsync(string origin, ProviderTokens tokens);
    Task TurnOffAsync(string origin, ProviderTokens tokens, string entityId);
    Task RevokeAsync(string origin, ProviderTokens tokens);
}
public interface ICredentialProtector
{
    string Protect(ProviderTokens tokens); ProviderTokens Unprotect(string value);
}
