using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

public sealed class UsageLimitReachedService : IUsageLimitReachedService
{
    public IReadOnlyList<TurnOffRecommendation> RecommendActions(double currentWatts, double limitWatts, IEnumerable<Device> devices)
    {
        if (!double.IsFinite(limitWatts) || limitWatts <= 0)
            throw new ArgumentOutOfRangeException(nameof(limitWatts), "The usage limit must be finite and positive.");
        if (!double.IsFinite(currentWatts) || currentWatts < limitWatts)
            return [];

        List<TurnOffRecommendation> actions = [];
        double reductionWatts = 0;
        // Taking the largest loads first minimizes the number of devices interrupted.
        foreach (var device in devices
            .Where(d => d.Present && d.Allowed && !SmartPanelCircuit.IsCircuit(d.EntityId) && d.ShutoffLevel != ShutoffLevels.Never && !d.EntityId.StartsWith("climate.", StringComparison.Ordinal)
                && d.State is not ("off" or "unknown" or "unavailable")
                && d.PowerWatts is { } watts && double.IsFinite(watts) && watts > 0)
            .OrderByDescending(d => d.PowerWatts)
            .ThenBy(d => d.EntityId, StringComparer.Ordinal))
        {
            var watts = device.PowerWatts!.Value;
            actions.Add(new(device.EntityId, watts));
            reductionWatts += watts;
            if (currentWatts - reductionWatts < limitWatts)
                break;
        }
        return actions;
    }
}
