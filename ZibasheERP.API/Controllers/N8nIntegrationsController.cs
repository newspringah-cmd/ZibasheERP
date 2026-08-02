using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.Application.Features.Integrations.RecordOrderArtifact;
using ZibasheERP.Domain.Entities;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/integrations/n8n")]
[Authorize(Roles = "N8n")]
public sealed class N8nIntegrationsController : ControllerBase
{
    private readonly IMediator _mediator;

    public N8nIntegrationsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("order-artifacts")]
    public async Task<IActionResult> RecordOrderArtifact(
        RecordOrderArtifactRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<OrderArtifactType>(request.Type, true, out var type) ||
            !Enum.IsDefined(type))
        {
            return BadRequest(new { Message = "نوع فایل سفارش معتبر نیست." });
        }

        return Ok(await _mediator.Send(
            new RecordOrderArtifactCommand(
                request.SourceEventId,
                request.OrderId,
                type,
                request.FileUrl,
                request.ExternalFileId,
                request.ContentType),
            cancellationToken));
    }

    public sealed record RecordOrderArtifactRequest(
        Guid SourceEventId,
        Guid OrderId,
        string Type,
        string? FileUrl,
        string? ExternalFileId,
        string? ContentType);
}
