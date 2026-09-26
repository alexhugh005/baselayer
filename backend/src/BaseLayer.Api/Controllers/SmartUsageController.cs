using BaseLayer.Application.Contracts;
using BaseLayer.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace BaseLayer.Api.Controllers;

[ApiController, Authorize, Route("api/homes/{homeId:guid}/smart-usage")]
public sealed class SmartUsageController(ISmartPowerUsageService service) : ControllerBase
{
    private string Owner => User.FindFirst("sub")?.Value ?? throw new UnauthorizedAccessException();
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<SmartUsagePlan>> Preview(Guid homeId, CancellationToken cancellationToken, [FromQuery] double? targetWatts = null) =>
        Ok(await service.PreviewAsync(Owner, homeId, targetWatts, cancellationToken));
    [HttpPost]
    public async Task<ActionResult<List<CommandDto>>> Apply(Guid homeId, ApplySmartUsageRequest request, CancellationToken cancellationToken) =>
        Ok(await service.ApplyAsync(Owner, homeId, request, cancellationToken));
}
