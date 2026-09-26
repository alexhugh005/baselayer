using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace BaseLayer.Api.Controllers;

[ApiController, Authorize, Route("api/homes/{homeId:guid}/battery")]
public sealed class BatteryController(IBatteryService service) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<BatteryStatusDto>> Get(Guid homeId, CancellationToken cancellationToken)
    {
        var ownerId = User.FindFirst("sub")?.Value ?? throw new UnauthorizedAccessException();
        return Ok(await service.GetCurrentAsync(ownerId, homeId, cancellationToken));
    }
}
