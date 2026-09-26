using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
namespace BaseLayer.Application.Services;

public sealed class BatteryService(IPlatformRepository repository, IBatteryProvider provider) : IBatteryService
{
    public async Task<BatteryStatusDto> GetCurrentAsync(string ownerId, Guid homeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var home = await repository.HomeAsync(homeId);
        if (home is null || home.OwnerId != ownerId)
            throw new KeyNotFoundException("Home not found.");

        BatterySnapshot snapshot;
        try
        {
            snapshot = await provider.ReadAsync(homeId, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new BatteryProviderException("Battery telemetry is unavailable. Try again later.", exception);
        }

        // Validate at the boundary so an external adapter cannot expose invalid telemetry.
        if (snapshot is null || !double.IsFinite(snapshot.CapacityKwh) || snapshot.CapacityKwh <= 0 ||
            !double.IsFinite(snapshot.StoredEnergyKwh) || snapshot.StoredEnergyKwh < 0 ||
            snapshot.StoredEnergyKwh > snapshot.CapacityKwh || snapshot.ObservedAtUtc == default)
            throw new BatteryProviderException("The battery provider returned invalid telemetry.");

        return new(homeId, snapshot.CapacityKwh, snapshot.StoredEnergyKwh,
            snapshot.StoredEnergyKwh / snapshot.CapacityKwh * 100,
            snapshot.ObservedAtUtc.ToUniversalTime(), snapshot.IsSimulated);
    }
}
