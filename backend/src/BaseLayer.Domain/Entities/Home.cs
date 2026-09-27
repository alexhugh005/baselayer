namespace BaseLayer.Domain.Entities;

public sealed class Home
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string GridOutageRisk { get; set; } = "low";
    public DateTime? LastRestoreUtc { get; set; }
    public bool AnomalySavingsEnabled { get; set; }
    public List<UsageAnomaly> Anomalies { get; set; } = [];
    public bool SmartPowerOffEnabled { get; set; }
    public string? SmartPowerOffEventId { get; set; }
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
    public string EvVehiclesJson { get; set; } = "[]";
    public string EvBatterySensorsJson { get; set; } = "[]";
    public string CurrentControlsJson { get; set; } = "[]";
    public string CircuitPrioritiesJson { get; set; } = "[]";
    public string PowerSupplyJson { get; set; } = "[]";
    public string OutageRecoveryJson { get; set; } = "{}";
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
public static class ShutoffLevels
{
    public const string Never = "Never";
    public const string Sometimes = "Sometimes";
    public const string Anytime = "Anytime";
}
public sealed class Device
{
    public string? Category { get; set; }
    public double? StandardWattsOverride { get; set; }
    public bool StandardPowerAnomalyActive { get; set; }
    public string? EvBatterySettingsJson { get; set; }
    public string? EvCurrentEntityId { get; set; }
    public double EvWattsPerAmp { get; set; } = 240;
    public int PowerSensorRevision { get; set; }
    public double? LastOnWatts { get; set; }
    public bool SmartUsageHeld { get; set; }
    public RestoreQueueEntry? RestoreEntry { get; set; }
    public string ShutoffLevel { get; set; } = ShutoffLevels.Sometimes;
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
    public bool StartCharge { get; set; }
    public DateTime? ChargeStartSentUtc { get; set; }
    public string? EvVehicleId { get; set; }
    public string? ChargeLimitControlEntityId { get; set; }
    public double? ChargeLimitPercent { get; set; }
    public Guid? AnomalyId { get; set; }
    public string? OutageEventId { get; set; }
    public string? PriorityControlEntityId { get; set; }
    public string? CircuitPriority { get; set; }
    public string? CurrentControlEntityId { get; set; }
    public double? CurrentAmps { get; set; }
    public double? PreviousCurrentAmps { get; set; }
    public bool IsRestoration { get; set; }
    public bool ManualCircuit { get; set; }
    public double? UsageBudgetWatts { get; set; }
    public string? UsageRevision { get; set; }
    public string Action { get; set; } = "Off";
    public double? EstimatedWatts { get; set; }
    public bool Automatic { get; set; }
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
