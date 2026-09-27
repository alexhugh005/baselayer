using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;

namespace BaseLayer.Application.Services;

// Replace this stub with the grid signal provider when it is available.
public sealed class GridOutageDetectionService : IGridOutageDetectionService
{
    public Task<string> GetRiskAsync(Guid homeId) => Task.FromResult(GridOutageRisk.Low);
}
