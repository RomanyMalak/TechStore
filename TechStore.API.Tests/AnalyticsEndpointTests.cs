using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Analytics;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;

namespace TechStore.API.Tests;

public sealed class AnalyticsEndpointTests(AnalyticsEndpointTests.AnalyticsFactory factory)
    : IClassFixture<AnalyticsEndpointTests.AnalyticsFactory>
{
    [Fact]
    public async Task Guest_IsUnauthorized_BeforeAnalyticsReads()
    {
        using var client = factory.Client();
        var before = factory.Repository.Invocations.Count;
        var response = await client.GetAsync("/api/admin/analytics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, factory.Repository.Invocations.Count);
    }

    [Fact]
    public async Task Customer_IsForbidden_BeforeAnalyticsReads()
    {
        using var client = await factory.CustomerClientAsync();
        var before = factory.Repository.Invocations.Count;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/analytics")).StatusCode);
        Assert.Equal(before, factory.Repository.Invocations.Count);
    }

    [Fact]
    public async Task Admin_BindsQuery_ReturnsSafeRealContract_WithUtcAndNoStore()
    {
        using var client = await factory.AdminClientAsync();
        var response = await client.GetAsync("/api/admin/analytics?periodDays=7&pageNumber=2&pageSize=3&userId=ignored");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<AnalyticsResponseDto>())!;
        Assert.Equal(7, result.PeriodDays);
        Assert.Equal(TimeSpan.FromDays(7), result.ToUtc - result.FromUtc!.Value);
        Assert.Equal(DateTimeKind.Utc, result.ToUtc.Kind);
        Assert.Equal(2, result.Performance.PageNumber);
        Assert.Equal(3, result.Performance.PageSize);
        factory.Repository.Verify(repo => repo.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime>(), 2, 3,
            It.IsAny<CancellationToken>()), Times.Once);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "cartCoverageComplete", "cartTrackingStartedAtUtc", "fromUtc", "mostAdded", "mostOrdered", "overview", "performance", "periodDays", "toUtc" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        var body = json.RootElement.GetRawText();
        foreach (var name in new[] { "ownerHash", "userId", "guestKey", "clientRequestId", "password", "accessToken" })
            Assert.DoesNotContain('"' + name + '"', body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("periodDays=1")]
    [InlineData("pageNumber=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageNumber=2147483647&pageSize=100")]
    public async Task InvalidQuery_UsesApplicationValidation_WithoutReadingRepository(string query)
    {
        using var client = await factory.AdminClientAsync();
        var before = factory.Repository.Invocations.Count;
        var response = await client.GetAsync("/api/admin/analytics?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, factory.Repository.Invocations.Count);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MissingTracking_ReturnsExpectedConflict_AndSwaggerDocumentsAdminAccess()
    {
        using var client = await factory.AdminClientAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/admin/analytics?pageNumber=9")).StatusCode);
        using var swagger = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var operation = swagger.RootElement.GetProperty("paths").GetProperty("/api/admin/analytics").GetProperty("get");
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        foreach (var status in new[] { "200", "400", "401", "403", "409" })
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
    }

    public sealed class AnalyticsFactory : ApiFactory
    {
        public Mock<IAnalyticsRepository> Repository { get; } = new(MockBehavior.Strict);
        public AnalyticsFactory()
        {
            Repository.Setup(repo => repo.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime>(), It.IsAny<int>(),
                    It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime? _, DateTime to, int page, int size, CancellationToken _) =>
                    new AnalyticsReadResult(to.AddDays(-100), new(0, 0, 0, 0, 0), [], [], new([], 0, page, size)));
            Repository.Setup(repo => repo.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime>(), 9,
                    It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConflictException("Cart analytics tracking is not initialized."));
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAnalyticsRepository>();
                services.AddScoped(_ => Repository.Object);
            });
        }
    }
}
