using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public static class AnomalySavingsPolicy
{
    // Product assumption: excess consumption would otherwise continue for 15 days.
    public const int AssumedUndetectedMinutes = 15 * 24 * 60;

    public static double EstimateKwh(UsageAnomaly anomaly) =>
        anomaly.ConfirmedUtc != null && double.IsFinite(anomaly.AvoidedWatts) && anomaly.AvoidedWatts > 0
            ? anomaly.AvoidedWatts / 1000 * (AssumedUndetectedMinutes / 60.0)
            : 0;
}
