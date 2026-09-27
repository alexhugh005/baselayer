namespace BaseLayer.Domain.Entities;

public static class GridOutageRisk
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public static bool RequiresReduction(Home home) =>
        (home.SmartPowerOffEnabled && home.AlwaysKeepBelowBatteryLimit) || RequiresReduction(home.GridOutageRisk);
    public static bool RequiresReduction(string risk) => risk is Medium or High;
}
