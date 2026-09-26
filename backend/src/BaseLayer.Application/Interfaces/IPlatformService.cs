using BaseLayer.Application.Contracts;
namespace BaseLayer.Application.Interfaces;

public interface IPlatformService
{
    Task<List<HomeDto>> HomesAsync(string owner);
    Task<OAuthStartResult> StartAsync(string owner, OAuthStartRequest request);
    Task<HomeDto> CompleteAsync(string owner, OAuthCompleteRequest request);
    Task<HomeDto> SettingsAsync(string owner, Guid id, HomeSettingsRequest request);
    Task<List<CommandDto>> TurnOffAsync(string owner, Guid homeId, TurnOffRequest request);
    Task<CommandDto> CancelCommandAsync(string owner, Guid homeId, Guid commandId);
    Task DeleteAsync(string owner, Guid homeId);
    Task RevokeAsync(string owner, Guid homeId);
    Task PollAsync(Guid homeId);
}
