using BaseLayer.Application.Contracts;
namespace BaseLayer.Application.Interfaces;

public interface IBatteryService
{
    Task<BatteryStatusDto> GetCurrentAsync(string ownerId, Guid homeId, CancellationToken cancellationToken = default);
}
