using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.Application.Features.Reports.GetBusinessReport;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public sealed class ReportsController : ControllerBase
{
    private readonly IMediator _mediator;
    public ReportsController(IMediator mediator) => _mediator = mediator;

    [HttpGet("business")]
    public async Task<IActionResult> GetBusinessReport(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int topLimit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _mediator.Send(
                new GetBusinessReportQuery(from, to, topLimit),
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }
}
