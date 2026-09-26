using BaseLayer.Application.Contracts;
using BaseLayer.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace BaseLayer.Api.Controllers;

[ApiController, Authorize, Route("api/homes/{homeId:guid}/devices/{entityId}")]
public sealed class UsageHistoryController(UsageHistoryService service) : ControllerBase
{
    private string Owner => User.FindFirst("sub")?.Value ?? throw new UnauthorizedAccessException();

    [HttpGet("usage")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<DeviceUsageHistoryDto>> Usage(Guid homeId, string entityId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, [FromQuery] string resolution = "minute") =>
        Ok(await service.GetAsync(Owner, homeId, entityId, from, to, resolution));

    [HttpGet("state-history")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<List<DeviceStateEventDto>>> StateHistory(Guid homeId, string entityId,
        [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to) =>
        Ok(await service.GetStateEventsAsync(Owner, homeId, entityId, from, to));
}
