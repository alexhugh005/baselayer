using BaseLayer.Application.Contracts;
namespace BaseLayer.Application.Interfaces;

/// <summary>
/// Reads battery telemetry for an internal home ID. External adapters own the mapping
/// to their battery identifiers, credentials, transport, and vendor response format.
/// Missing telemetry must fail explicitly, never be represented as an empty battery.
/// </summary>
public interface IBatteryProvider
{
    Task<BatterySnapshot> ReadAsync(Guid homeId, CancellationToken cancellationToken = default);
}

public sealed class BatteryProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);
