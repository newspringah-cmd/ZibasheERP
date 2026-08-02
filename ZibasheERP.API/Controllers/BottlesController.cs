using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.Application.Features.Bottles.ManageBottles;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public sealed class BottlesController : ControllerBase
{
    private readonly IMediator _mediator;
    public BottlesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool includeInactive = false,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await _mediator.Send(new GetAdminBottlesQuery(includeInactive, limit), cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(CreateBottleCommand command, CancellationToken cancellationToken) =>
        await Execute(() => _mediator.Send(command, cancellationToken), true);

    [HttpPost("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(
        Guid id,
        SetBottleStatusRequest request,
        CancellationToken cancellationToken) =>
        await Execute(() => _mediator.Send(new SetBottleStatusCommand(id, request.IsActive), cancellationToken));

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
        catch (InvalidOperationException exception)
        {
            return Conflict(new { Message = exception.Message });
        }
    }

    public sealed record SetBottleStatusRequest(bool IsActive);
}
