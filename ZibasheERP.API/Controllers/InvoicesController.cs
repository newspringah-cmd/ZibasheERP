using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.Application.Features.Invoices.GetInvoice;
using ZibasheERP.Application.Features.Invoices.IssueInvoice;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,TelegramBot")]
public sealed class InvoicesController : ControllerBase
{
    private readonly IMediator _mediator;

    public InvoicesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("order/{orderId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Issue(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await _mediator.Send(
                new IssueInvoiceCommand(orderId),
                cancellationToken);
            return StatusCode(StatusCodes.Status201Created, invoice);
        }
        catch (ValidationException exception)
        {
            return ValidationProblem(ToErrors(exception));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }

    [HttpGet("{invoiceId:guid}")]
    public async Task<IActionResult> Get(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await _mediator.Send(
            new GetInvoiceQuery(invoiceId),
            cancellationToken);
        return invoice is null
            ? NotFound(new { Message = "فاکتور پیدا نشد." })
            : Ok(invoice);
    }

    private static ValidationProblemDetails ToErrors(ValidationException exception)
    {
        var errors = exception.Errors
            .GroupBy(error => error.PropertyName)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.ErrorMessage).ToArray());
        return new ValidationProblemDetails(errors);
    }
}
