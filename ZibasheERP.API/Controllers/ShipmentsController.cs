using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Features.Shipments.CreateShipment;
using ZibasheERP.Application.Features.Shipments.MarkDelivered;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public sealed class ShipmentsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ShipmentsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateShipmentCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediator.Send(command, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (ValidationException exception)
        {
            var errors = exception.Errors
                .GroupBy(error => error.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.ErrorMessage).ToArray());
            return ValidationProblem(new ValidationProblemDetails(errors));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { Message = "وضعیت سفارش هم‌زمان تغییر کرده است؛ دوباره تلاش کنید." });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }

    [HttpPost("{shipmentId:guid}/delivered")]
    public async Task<IActionResult> MarkDelivered(
        Guid shipmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _mediator.Send(
                new MarkShipmentDeliveredCommand(shipmentId),
                cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { Message = "وضعیت مرسوله هم‌زمان تغییر کرده است؛ دوباره تلاش کنید." });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }
}
