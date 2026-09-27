using BaseLayer.Application.Contracts;
namespace BaseLayer.Application.Interfaces;

public sealed record ProviderTokens(string AccessToken, string RefreshToken, DateTime ExpiresUtc);
public sealed record ProviderDevice(string EntityId, string Name, string State);
public sealed record ProviderSnapshot(List<ProviderDevice> Devices, List<PowerSensorDto> Sensors, Dictionary<string, double?> Power, List<CurrentControlDto>? CurrentControls = null, List<CircuitPriorityDto>? CircuitPriorities = null, List<PowerSupplyReading>? PowerSupplies = null, Dictionary<string, double>? CircuitRestoreEstimates = null, bool? BackupReady = null, Dictionary<string, List<string>>? CircuitDevices = null, Dictionary<string, bool?>? CircuitSupplyStates = null, List<EvBatterySensorDto>? EvBatterySensors = null);
public interface ISmartHomeProvider
{
    string ValidateOrigin(string origin);
    string AuthorizationUrl(string origin, string state);
    Task<ProviderTokens> ExchangeAsync(string origin, string code);
    Task<ProviderTokens> RefreshAsync(string origin, ProviderTokens tokens);
    Task<ProviderSnapshot> ReadAsync(string origin, ProviderTokens tokens);
    Task SetChargeLimitAsync(string origin, ProviderTokens tokens, string entityId, double percent) => throw new NotSupportedException("Charge limit control is unsupported.");
    Task SetCurrentAsync(string origin, ProviderTokens tokens, string entityId, double amps);
    Task SetCircuitPriorityAsync(string origin, ProviderTokens tokens, string entityId, string priority);
    Task TurnOnAsync(string origin, ProviderTokens tokens, string entityId);
    Task RestoreDeviceStateAsync(string origin, ProviderTokens tokens, string entityId, string state) =>
        state == "on" ? TurnOnAsync(origin, tokens, entityId) : throw new NotSupportedException("Device state restoration is unsupported.");
    Task TurnOffAsync(string origin, ProviderTokens tokens, string entityId);
    Task RevokeAsync(string origin, ProviderTokens tokens);
}
public interface ICredentialProtector
{
    string Protect(ProviderTokens tokens); ProviderTokens Unprotect(string value);
}
