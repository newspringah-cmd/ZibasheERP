using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ZibasheERP.API.Diagnostics;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            ValidationException => (
                StatusCodes.Status400BadRequest,
                "Validation failed",
                "One or more request values are invalid."),
            DbUpdateConcurrencyException => (
                StatusCodes.Status409Conflict,
                "Concurrent update",
                "The data changed at the same time. Please retry the operation."),
            InvalidOperationException => (
                StatusCodes.Status400BadRequest,
                "Operation rejected",
                exception.Message),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Unexpected server error",
                "An unexpected error occurred. Use the trace ID when contacting support.")
        };

        if (status >= 500)
            _logger.LogError(exception, "Unhandled API error. TraceId: {TraceId}", httpContext.TraceIdentifier);
        else
            _logger.LogWarning(exception, "API request rejected. TraceId: {TraceId}", httpContext.TraceIdentifier);

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path,
            Extensions = { ["traceId"] = httpContext.TraceIdentifier }
        }, cancellationToken);
        return true;
    }
}
