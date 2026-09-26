using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace BaseLayer.Api.Controllers;

[ApiController, Authorize, Route("api/homes")]
public sealed class HomesController(IPlatformService service) : ControllerBase
{
    private string Owner => User.FindFirst("sub")?.Value ?? throw new UnauthorizedAccessException();
    [HttpGet] public async Task<IActionResult> List() => Ok(await service.HomesAsync(Owner));
    [HttpPut("{id:guid}/settings")] public async Task<IActionResult> Settings(Guid id, HomeSettingsRequest request) => Ok(await service.SettingsAsync(Owner, id, request));
    [HttpPost("{id:guid}/commands")] public async Task<IActionResult> TurnOff(Guid id, TurnOffRequest request) => Ok(await service.TurnOffAsync(Owner, id, request));
    [HttpDelete("{id:guid}/commands/{commandId:guid}")]
    public async Task<IActionResult> CancelCommand(Guid id, Guid commandId) => Ok(await service.CancelCommandAsync(Owner, id, commandId));
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await service.DeleteAsync(Owner, id);
        return NoContent();
    }
    [HttpDelete("{id:guid}/connection")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        await service.RevokeAsync(Owner, id);
        return NoContent();
    }
}
[ApiController, Authorize, Route("api/connections/home-assistant")]
public sealed class ConnectionsController(IPlatformService service) : ControllerBase
{
    private string Owner => User.FindFirst("sub")?.Value ?? throw new UnauthorizedAccessException();
    [HttpPost("start")] public async Task<IActionResult> Start(OAuthStartRequest request) => Ok(await service.StartAsync(Owner, request));
    [HttpPost("complete")] public async Task<IActionResult> Complete(OAuthCompleteRequest request) => Ok(await service.CompleteAsync(Owner, request));
}
