using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using FluentValidation;
using ZibasheERP.Application.Features.Orders.CreateOrder;
using ZibasheERP.Application.Features.Orders.AdvanceFulfillment;
using ZibasheERP.Application.Features.Orders.GetCustomerOrders;
using ZibasheERP.Application.Features.Orders.GetOrder;
using ZibasheERP.Application.Features.Orders.GetAdminOrders;
using ZibasheERP.Application.Features.Orders.CancelOrder;
using ZibasheERP.Application.Features.Shipments.GetShipmentTracking;
using ZibasheERP.Application.Features.Integrations.GetOrderArtifacts;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,TelegramBot")]
public class OrdersController : ControllerBase
{
    private readonly IMediator _mediator;

    public OrdersController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var orderId = await _mediator.Send(
                command,
                cancellationToken);

            return CreatedAtAction(
                nameof(GetById),
                new { id = orderId },
                new
                {
                    OrderId = orderId,
                    Message = "سفارش با موفقیت ثبت شد."
                });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new
            {
                Message = exception.Message
            });
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
            return Conflict(new
            {
                Message = "اطلاعات اعتبار یا ظرفیت لیست هم‌زمان تغییر کرده است؛ سفارش را دوباره ارسال کنید."
            });
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var order = await _mediator.Send(
            new GetOrderQuery(id),
            cancellationToken);

        return order is null
            ? NotFound(new { Message = "سفارش پیدا نشد." })
            : Ok(order);
    }

    [HttpGet("{id:guid}/artifacts")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetArtifacts(
        Guid id,
        CancellationToken cancellationToken) =>
        Ok(await _mediator.Send(new GetOrderArtifactsQuery(id), cancellationToken));

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetForAdmin(
        [FromQuery] ZibasheERP.Domain.Entities.OrderStatus? status,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await _mediator.Send(
            new GetAdminOrdersQuery(status, limit),
            cancellationToken));

    [HttpGet("by-telegram/{telegramId}")]
    public async Task<IActionResult> GetByTelegramId(
        string telegramId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(telegramId) || telegramId.Length > 50)
        {
            return BadRequest(new
            {
                Message = "شناسه تلگرام معتبر نیست."
            });
        }

        var orders = await _mediator.Send(
            new GetCustomerOrdersQuery(null, telegramId),
            cancellationToken);

        return Ok(orders);
    }

    [HttpGet("tracking/{orderNumber}/by-telegram/{telegramId}")]
    public async Task<IActionResult> GetTrackingByTelegram(
        string orderNumber,
        string telegramId,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new GetShipmentTrackingQuery(orderNumber, null, telegramId),
            cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("tracking/{orderNumber}/by-customer/{customerId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetTrackingByCustomer(
        string orderNumber,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(
            new GetShipmentTrackingQuery(orderNumber, customerId, null),
            cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{id:guid}/decant")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> MarkDecanted(Guid id, CancellationToken cancellationToken) =>
        Advance(id, ZibasheERP.Domain.Entities.OrderStatus.Decanted, cancellationToken);

    [HttpPost("{id:guid}/ready-to-ship")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> MarkReadyToShip(Guid id, CancellationToken cancellationToken) =>
        Advance(id, ZibasheERP.Domain.Entities.OrderStatus.ReadyToShip, cancellationToken);

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Cancel(
        Guid id,
        CancelOrderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _mediator.Send(
                new CancelOrderCommand(id, request.Reason),
                cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { Message = "سفارش، اعتبار مشتری یا ظرفیت لیست هم‌زمان تغییر کرده است." });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }

    private async Task<IActionResult> Advance(
        Guid id,
        ZibasheERP.Domain.Entities.OrderStatus targetStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _mediator.Send(
                new AdvanceFulfillmentCommand(id, targetStatus),
                cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }

    public sealed record CancelOrderRequest(string Reason);
}
