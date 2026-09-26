using BaseLayer.Application.Contracts;
namespace BaseLayer.Application.Interfaces;

public interface ISmartPowerUsageService
{
    Task<SmartUsagePlan> PreviewAsync(string owner, Guid homeId, double? targetWatts = null, CancellationToken cancellationToken = default);
    Task<List<CommandDto>> ApplyAsync(string owner, Guid homeId, ApplySmartUsageRequest request, CancellationToken cancellationToken = default);
}
