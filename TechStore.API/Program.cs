using TechStore.Application;
using TechStore.API.Configuration;
using TechStore.API.Errors;
using TechStore.Infrastructure;

namespace TechStore.API
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddApplication();
            builder.Services.AddInfrastructure(builder.Configuration);
            builder.Services.AddApi();

            var app = builder.Build();

            // A missing socket peer must never make caller-controlled forwarding headers trusted.
            app.UseWhen(context => context.Connection.RemoteIpAddress is not null,
                pipeline => pipeline.UseForwardedHeaders());
            app.UseExceptionHandler();
            app.UseStatusCodePages(context => ApiProblemWriter.WriteAsync(context.HttpContext,
                ApiProblemWriter.Create(context.HttpContext, context.HttpContext.Response.StatusCode, "The request could not be completed."),
                context.HttpContext.RequestAborted));
            app.UseHttpsRedirection();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.MapControllers();

            app.Run();
        }
    }
}
