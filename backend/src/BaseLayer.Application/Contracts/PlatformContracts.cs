namespace BaseLayer.Application.Contracts;

public sealed record OAuthStartRequest(string Name, string BaseUrl);
public sealed record OAuthStartResult(string AuthorizationUrl, string State, DateTime ExpiresUtc);
public sealed record OAuthCompleteRequest(string Code, string State);
public sealed record HomeSettingsRequest(bool AllowAll, bool AllowFutureDevices, List<string> AllowedEntityIds, string? HouseholdPowerSensorId, Dictionary<string, string?> DevicePowerSensors, string PowerSource = "wholeHouseMeter");
public sealed record TurnOffRequest(List<string> EntityIds, string IdempotencyKey);
public sealed record PowerSensorDto(string EntityId, string Name, string Unit);
public sealed record DeviceDto(string EntityId, string Name, string State, double? PowerWatts, bool Allowed, bool Recommended, string? PowerSensorId);
public sealed record CommandDto(Guid Id, string EntityId, string Status, int Attempts, DateTime CreatedUtc, string? Message);
public sealed record HomeDto(Guid Id, string Name, bool Connected, bool Revoked, DateTime? LastSeenUtc, double? HouseholdWatts, double LimitWatts, double? ProjectedWatts, List<DeviceDto> Devices, List<CommandDto> Commands, string BaseUrl, string? HouseholdPowerSensorId, bool AllowFutureDevices, List<PowerSensorDto> PowerSensors, string PowerSource = "wholeHouseMeter");
