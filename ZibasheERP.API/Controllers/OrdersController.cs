using MediatR;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.Application.Features.Orders.CreateOrder;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
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
    }

    [HttpGet("{id:guid}")]
    public IActionResult GetById(Guid id)
    {
        return Ok(new
        {
            OrderId = id
        });
    }
}