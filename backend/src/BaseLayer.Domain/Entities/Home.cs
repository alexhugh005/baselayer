namespace BaseLayer.Domain.Entities;

public sealed class Home
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string PowerSource { get; set; } = HouseholdPowerSources.WholeHouseMeter;
    public string? ProtectedTokens
    {
        get; set;
    }
    public string? HouseholdPowerSensorId
    {
        get; set;
    }
    public bool AllowFutureDevices
    {
        get; set;
    }
    public string SensorsJson { get; set; } = "[]";
    public DateTime? LastSeenUtc
    {
        get; set;
    }
    public double? HouseholdWatts
    {
        get; set;
    }
    public bool Revoked
    {
        get; set;
    }
    public List<Device> Devices { get; set; } = [];
    public List<DeviceCommand> Commands { get; set; } = [];
}
public static class HouseholdPowerSources
{
    public const string WholeHouseMeter = "wholeHouseMeter";
    public const string DeviceSum = "deviceSum";
}
public sealed class Device
{
    public double ThermostatMinF { get; set; } = 66;
    public double ThermostatMaxF { get; set; } = 80;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HomeId
    {
        get; set;
    }
    public string EntityId { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "unknown";
    public double? PowerWatts
    {
        get; set;
    }
    public string? PowerSensorId
    {
        get; set;
    }
    public bool Allowed
    {
        get; set;
    }
    public bool Present
    {
        get; set;
    }
}
public sealed class OAuthState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresUtc
    {
        get; set;
    }
    public bool Used
    {
        get; set;
    }
}
public sealed class DeviceCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HomeId
    {
        get; set;
    }
    public string EntityId { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int Attempts
    {
        get; set;
    }
    public DateTime CreatedUtc
    {
        get; set;
    }
    public DateTime ExpiresUtc
    {
        get; set;
    }
    public DateTime NextAttemptUtc
    {
        get; set;
    }
    public DateTime? LeaseUntilUtc
    {
        get; set;
    }
    public string? AttemptToken
    {
        get; set;
    }
    public string? Message
    {
        get; set;
    }
}
