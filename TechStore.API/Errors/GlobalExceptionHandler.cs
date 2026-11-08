using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechStore.Application.Exceptions;

namespace TechStore.API.Errors;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem = exception switch
        {
            CommerceConflictException commerce => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict, Title = "The cart or checkout changed.", Type = "about:blank",
                Extensions = { ["code"] = commerce.Code }
            },
            ValidationException validation => new HttpValidationProblemDetails(validation.Errors.ToDictionary(pair => pair.Key, pair => pair.Value))
            {
                Status = StatusCodes.Status400BadRequest, Title = "One or more validation errors occurred.", Type = "about:blank"
            },
            NotFoundException => ApiProblemWriter.Create(httpContext, StatusCodes.Status404NotFound, "Resource not found.", exception.Message),
            ConflictException => ApiProblemWriter.Create(httpContext, StatusCodes.Status409Conflict, "The request conflicts with the current state.", exception.Message),
            DbUpdateConcurrencyException => ApiProblemWriter.Create(httpContext, StatusCodes.Status409Conflict, "The record changed. Reload it and retry."),
            UnauthorizedException => ApiProblemWriter.Create(httpContext, StatusCodes.Status401Unauthorized, "Authentication failed.", exception.Message),
            ForbiddenException => ApiProblemWriter.Create(httpContext, StatusCodes.Status403Forbidden, "Insufficient permissions."),
            _ => ApiProblemWriter.Create(httpContext, StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };
        if (problem.Status == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled request exception. TraceId: {TraceId}", httpContext.TraceIdentifier);
        ApiProblemWriter.Enrich(httpContext, problem);
        await ApiProblemWriter.WriteAsync(httpContext, problem, cancellationToken);
        return true;
    }
}
