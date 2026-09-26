namespace BaseLayer.Api.Providers;

public sealed class SimulatedBatteryOptions
{
    public const string SectionName = "Battery:Simulation";
    public double CapacityKwh { get; set; } = 13.5;
    public double InitialStateOfChargePercent { get; set; } = 80;
    /// <summary>Positive values discharge; negative values charge; zero holds the initial level.</summary>
    public double NetPowerWatts { get; set; } = 1000;

    public bool IsValid() => double.IsFinite(CapacityKwh) && CapacityKwh > 0 &&
        double.IsFinite(InitialStateOfChargePercent) && InitialStateOfChargePercent is >= 0 and <= 100 &&
        double.IsFinite(NetPowerWatts);
}
