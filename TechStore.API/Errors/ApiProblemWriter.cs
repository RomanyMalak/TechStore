using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace TechStore.API.Errors;

public static class ApiProblemWriter
{
    public static ProblemDetails Create(HttpContext context, int status, string title, string? detail = null) =>
        Enrich(context, new ProblemDetails { Status = status, Title = title, Detail = detail, Type = "about:blank" });

    public static T Enrich<T>(HttpContext context, T problem) where T : ProblemDetails
    {
        problem.Instance = context.Request.Path;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        return problem;
    }

    public static Task WriteAsync(HttpContext context, ProblemDetails problem, CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return context.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null,
            contentType: "application/problem+json", cancellationToken: cancellationToken);
    }
}
