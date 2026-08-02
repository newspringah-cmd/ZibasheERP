using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Features.Payments.ConfirmPayment;
using ZibasheERP.Application.Features.Payments.GetPendingPayments;
using ZibasheERP.Application.Features.Payments.SubmitPayment;
using ZibasheERP.Application.Features.Payments.RejectPayment;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class PaymentsController : ControllerBase
{
    private readonly IMediator _mediator;

    public PaymentsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("pending")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetPending(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await _mediator.Send(
            new GetPendingPaymentsQuery(limit),
            cancellationToken));

    [HttpPost]
    [Authorize(Roles = "Admin,TelegramBot")]
    public async Task<IActionResult> Submit(
        SubmitPaymentCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediator.Send(command, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, result);
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

    [HttpPost("{paymentId:guid}/confirm")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Confirm(
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mediator.Send(
                new ConfirmPaymentCommand(paymentId),
                cancellationToken);
            return Ok(result);
        }
        catch (ValidationException exception)
        {
            return ValidationProblem(ToErrors(exception));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                Message = "وضعیت پرداخت یا بدهی مشتری هم‌زمان تغییر کرده است؛ دوباره تلاش کنید."
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }

    [HttpPost("{paymentId:guid}/reject")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reject(
        Guid paymentId,
        RejectPaymentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _mediator.Send(
                new RejectPaymentCommand(paymentId, request.Reason),
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
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

    public sealed record RejectPaymentRequest(string Reason);
}
