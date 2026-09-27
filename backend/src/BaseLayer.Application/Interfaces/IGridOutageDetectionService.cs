namespace BaseLayer.Application.Interfaces;

public interface IGridOutageDetectionService
{
    Task<string> GetRiskAsync(Guid homeId);
}
