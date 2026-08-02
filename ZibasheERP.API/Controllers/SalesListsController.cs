using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZibasheERP.Application.Features.SalesLists.ManageSalesLists;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/sales-lists")]
[Authorize(Roles = "Admin")]
public sealed class SalesListsController : ControllerBase
{
    private readonly IMediator _mediator;

    public SalesListsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await _mediator.Send(new GetAdminSalesListsQuery(limit), cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(
        CreateSalesListCommand command,
        CancellationToken cancellationToken) =>
        await Execute(() => _mediator.Send(command, cancellationToken), true);

    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, CancellationToken cancellationToken) =>
        await Execute(() => _mediator.Send(new CloseSalesListCommand(id), cancellationToken));

    private async Task<IActionResult> Execute<T>(Func<Task<T>> action, bool created = false)
    {
        try
        {
            var result = await action();
            return created ? StatusCode(StatusCodes.Status201Created, result) : Ok(result);
        }
        catch (ValidationException exception)
        {
            var errors = exception.Errors.GroupBy(error => error.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage).ToArray());
            return ValidationProblem(new ValidationProblemDetails(errors));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { Message = "لیست فروش هم‌زمان تغییر کرده است؛ دوباره تلاش کنید." });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { Message = exception.Message });
        }
    }
}
