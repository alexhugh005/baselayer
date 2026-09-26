using BaseLayer.Application.Contracts;
namespace BaseLayer.Application.Interfaces;

public interface IPlatformService
{
    Task<List<HomeDto>> HomesAsync(string owner);
    Task<OAuthStartResult> StartAsync(string owner, OAuthStartRequest request);
    Task<HomeDto> CompleteAsync(string owner, OAuthCompleteRequest request);
    Task<HomeDto> SettingsAsync(string owner, Guid id, HomeSettingsRequest request);
    Task<HomeDto> SmartPowerOffAsync(string owner, Guid id, SmartPowerOffRequest request);
    Task<CommandDto> EvCurrentAsync(string owner, Guid homeId, EvCurrentRequest request);
    Task<CommandDto> CircuitCommandAsync(string owner, Guid homeId, CircuitCommandRequest request);
    Task<CommandDto> CircuitPriorityAsync(string owner, Guid homeId, CircuitPriorityRequest request);
    Task<List<CommandDto>> TurnOffAsync(string owner, Guid homeId, TurnOffRequest request);
    Task<CommandDto> CancelCommandAsync(string owner, Guid homeId, Guid commandId);
    Task<HomeDto> KeepOffAsync(string owner, Guid homeId, string entityId);
    Task DeleteAsync(string owner, Guid homeId);
    Task RevokeAsync(string owner, Guid homeId);
    Task PollAsync(Guid homeId);
}
