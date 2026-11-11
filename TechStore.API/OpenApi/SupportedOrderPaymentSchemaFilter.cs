using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using TechStore.Application.DTOs.Orders;
using TechStore.Domain.Enums;

namespace TechStore.API.OpenApi;

public sealed class SupportedOrderPaymentSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(CreateOrderDto) || !schema.Properties.ContainsKey("paymentMethod")) return;
        // Restrict the request contract without changing enum values in historical order responses.
        schema.Properties["paymentMethod"] = new OpenApiSchema
        {
            Type = "integer", Format = "int32", Description = "Cash on delivery only. No online or simulated payment is supported.",
            Enum = new List<IOpenApiAny> { new OpenApiInteger((int)PaymentMethod.CashOnDelivery) },
            Default = new OpenApiInteger((int)PaymentMethod.CashOnDelivery)
        };
    }
}
