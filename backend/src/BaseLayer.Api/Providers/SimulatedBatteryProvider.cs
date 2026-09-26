using System.Collections.Concurrent;
using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using Microsoft.Extensions.Options;
namespace BaseLayer.Api.Providers;

/// <summary>
/// Each home's simulation starts on its first read and resets on API restart.
/// Elapsed monotonic time, rather than request count, determines the current level.
/// </summary>
public sealed class SimulatedBatteryProvider(IOptions<SimulatedBatteryOptions> options, TimeProvider clock) : IBatteryProvider
{
    private readonly SimulatedBatteryOptions settings = options.Value;
    private readonly ConcurrentDictionary<Guid, long> startedAt = new();

    public Task<BatterySnapshot> ReadAsync(Guid homeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = startedAt.GetOrAdd(homeId, _ => clock.GetTimestamp());
        var hours = Math.Max(0, clock.GetElapsedTime(start).TotalHours);
        var initialEnergy = settings.CapacityKwh * (settings.InitialStateOfChargePercent / 100);
        var storedEnergy = Math.Clamp(initialEnergy - settings.NetPowerWatts / 1000 * hours, 0, settings.CapacityKwh);
        return Task.FromResult(new BatterySnapshot(settings.CapacityKwh, storedEnergy, clock.GetUtcNow(), true));
    }
}
