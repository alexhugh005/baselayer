using BaseLayer.Application.Contracts;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Interfaces;

public interface IUsageLimitReachedService
{
    // Recommends the fewest shutoffs to get strictly below the limit, highest power first.
    // If the available power is insufficient, returns all eligible devices as best effort.
    IReadOnlyList<TurnOffRecommendation> RecommendActions(double currentWatts, double limitWatts, IEnumerable<Device> devices);
}
