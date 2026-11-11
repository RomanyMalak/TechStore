using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace TechStore.API.OpenApi;

public sealed class AuthorizationOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var bearer = new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>()
        };
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            // Browsing permits no credentials, while still accepting an Admin token in Swagger.
            operation.Security = new List<OpenApiSecurityRequirement> { new(), bearer };
        }
        else if (metadata.OfType<IAuthorizeData>().Any())
        {
            operation.Security = new List<OpenApiSecurityRequirement> { bearer };
            operation.Responses.TryAdd("401", Problem("Authentication required", typeof(ProblemDetails)));
            operation.Responses.TryAdd("403", Problem("Insufficient permissions", typeof(ProblemDetails)));
        }

        operation.Responses.TryAdd("400", Problem("Invalid request", typeof(HttpValidationProblemDetails)));
        operation.Responses.TryAdd("404", Problem("Resource not found", typeof(ProblemDetails)));
        operation.Responses.TryAdd("409", Problem("Duplicate value or concurrency conflict", typeof(ProblemDetails)));
        operation.Responses.TryAdd("500", Problem("Unexpected error", typeof(ProblemDetails)));
        if (metadata.OfType<EnableRateLimitingAttribute>().Any())
        {
            var limited = Problem("Too many requests", typeof(ProblemDetails));
            limited.Headers["Retry-After"] = new OpenApiHeader
            {
                Description = "Seconds before retrying the request.", Schema = new OpenApiSchema { Type = "integer", Format = "int32" }
            };
            operation.Responses.TryAdd("429", limited);
        }
        var ifMatch = operation.Parameters.FirstOrDefault(parameter => parameter.Name == "If-Match");
        if (ifMatch is not null)
        {
            ifMatch.Required = true;
            ifMatch.Description = "Copy the current product ETag from GET /api/products/{id}. Required for soft deletion; stale versions return 409.";
            ifMatch.Example = new OpenApiString("\"AAAAAAAAAAE=\"");
        }

        OpenApiResponse Problem(string description, Type type) => new()
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new() { Schema = context.SchemaGenerator.GenerateSchema(type, context.SchemaRepository) }
            }
        };
    }
}
