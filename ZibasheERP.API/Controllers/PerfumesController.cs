using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZibasheERP.Application.Features.Perfumes.CreatePerfume;
using ZibasheERP.Application.Features.Perfumes.GetPerfumes;
using ZibasheERP.Application.Features.Perfumes.ManagePerfume;

namespace ZibasheERP.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public sealed class PerfumesController : ControllerBase
{
    private readonly IMediator _mediator;

    public PerfumesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool includeInactive = false,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await _mediator.Send(
            new GetPerfumesQuery(includeInactive, limit),
            cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(
        CreatePerfumeCommand command,
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
        catch (InvalidOperationException exception)
        {
            return Conflict(new { Message = exception.Message });
        }
    }

    [HttpPost("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(
        Guid id,
        SetPerfumeStatusRequest request,
        CancellationToken cancellationToken) =>
        await Execute(() => _mediator.Send(
            new SetPerfumeStatusCommand(id, request.IsActive), cancellationToken));

    [HttpPost("{id:guid}/price")]
    public async Task<IActionResult> UpdatePrice(
        Guid id,
        UpdatePerfumePriceRequest request,
        CancellationToken cancellationToken) =>
        await Execute(() => _mediator.Send(
            new UpdatePerfumePriceCommand(id, request.PricePerMl), cancellationToken));

    private async Task<IActionResult> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (ValidationException exception)
        {
            var errors = exception.Errors.GroupBy(error => error.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage).ToArray());
            return ValidationProblem(new ValidationProblemDetails(errors));
        }
        catch (InvalidOperationException exception)
        {
            return NotFound(new { Message = exception.Message });
        }
    }

    public sealed record SetPerfumeStatusRequest(bool IsActive);
    public sealed record UpdatePerfumePriceRequest(decimal PricePerMl);
}
