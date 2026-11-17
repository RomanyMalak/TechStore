using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TechStore.Application.DTOs.Addresses;
using TechStore.Application.DTOs.Auth;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Interfaces.Services;
using TechStore.Application.Services;
using TechStore.Domain.Entities;

namespace TechStore.API.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class CustomerAccountSqlServerTests(SqlServerFixture database)
{
    [SqlServerFact]
    public async Task RealProfileAndAddresses_AreOwned_DefaultsStayUnique_AndOrderSnapshotsSurviveEdits()
    {
        var scenario = await database.SeedAsync(cart: false);
        await using var factory = new SqlApiFactory(database);
        var customer = await factory.CustomerAsync(); using var client = customer.Client;
        var other = await factory.CustomerAsync(); using var otherClient = other.Client;
        var profileResponse = await client.PutAsJsonAsync("/api/account/profile", new
        { FullName = "Updated SQL customer", PhoneNumber = "01012345678", Email = "ignored@example.test", Roles = new[] { "Admin" }, UserId = other.User.Id });
        profileResponse.EnsureSuccessStatusCode();
        var profile = (await profileResponse.Content.ReadFromJsonAsync<UserResponseDto>())!;
        Assert.Equal(customer.User.Id, profile.Id); Assert.Equal(customer.User.Email, profile.Email);
        Assert.Equal("Updated SQL customer", profile.FullName); Assert.Equal(new[] { "Customer" }, profile.Roles);
        var first = await CreateAddressAsync(client, Address("First"));
        var second = await CreateAddressAsync(client, Address("Second"));
        var foreign = await CreateAddressAsync(otherClient, Address("Foreign"));
        Assert.True(first.IsDefault); Assert.False(second.IsDefault);
        var path = $"/api/account/addresses/{second.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PutAsJsonAsync(path, Address("Forged"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PutAsync(path + "/default", null)).StatusCode);
        (await client.PutAsync(path + "/default", null)).EnsureSuccessStatusCode();
        Assert.Equal(second.Id, (await client.GetFromJsonAsync<List<AddressResponseDto>>("/api/account/addresses"))!.Single(address => address.IsDefault).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, Address("Second"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<AddressResponseDto>($"/api/account/addresses/{first.Id}"))!.IsDefault);
        (await client.PostAsJsonAsync("/api/cart/items", new AddToCartDto { ProductId = scenario.ProductId })).EnsureSuccessStatusCode();
        var quote = (await client.GetFromJsonAsync<CheckoutQuoteDto>("/api/checkout"))!;
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/orders", new CreateOrderDto
        { ClientRequestId = Guid.NewGuid(), QuoteVersion = quote.Version, ShippingAddressId = foreign.Id })).StatusCode);
        var placed = await client.PostAsJsonAsync("/api/orders", new CreateOrderDto
        { ClientRequestId = Guid.NewGuid(), QuoteVersion = quote.Version, ShippingAddressId = first.Id });
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var order = (await placed.Content.ReadFromJsonAsync<OrderResponseDto>())!;
        var changed = Address("Changed"); changed.IsDefault = true;
        (await client.PutAsJsonAsync($"/api/account/addresses/{first.Id}", changed)).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/api/account/addresses/{first.Id}")).EnsureSuccessStatusCode();
        var historical = (await client.GetFromJsonAsync<OrderResponseDto>($"/api/orders/{order.Id}"))!;
        Assert.Equal(order.ShippingAddress, historical.ShippingAddress);
        Assert.Equal(order.RecipientName, historical.RecipientName);
        Assert.Equal(order.ShippingPhone, historical.ShippingPhone);
        Assert.Empty((await client.GetFromJsonAsync<List<AddressResponseDto>>("/api/account/addresses"))!);
    }

    [SqlServerFact]
    public async Task ConcurrentDefaultChanges_KeepOneDefault_AndRetryAppliesTheRequestedSelection()
    {
        var scenario = await database.SeedAsync(cart: false);
        var addresses = await SeedAddressesAsync(scenario.UserId);
        await using var factory = new SqlApiFactory(database);
        var gate = new CommerceSqlServerTests.ReadGate();
        async Task<bool> Change(int id, CommerceSqlServerTests.ReadGate? synchronization)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var inner = scope.ServiceProvider.GetRequiredService<IAddressRepository>();
            var service = ActivatorUtilities.CreateInstance<CustomerAccountService>(scope.ServiceProvider, new GatedAddresses(inner, synchronization));
            try { await service.SetDefaultAddressAsync(scenario.UserId, id); return true; }
            catch (ConflictException) { return false; }
        }
        var results = await Task.WhenAll(Change(addresses[1].Id, gate), Change(addresses[2].Id, gate));
        Assert.Single(results, success => success);
        var retryId = results[0] ? addresses[2].Id : addresses[1].Id;
        Assert.True(await Change(retryId, null));
        await using var verify = database.Context();
        var defaults = await verify.Addresses.Where(address => address.UserId == scenario.UserId && address.IsDefault).ToListAsync();
        Assert.Equal(retryId, Assert.Single(defaults).Id);
        Assert.Equal(3, await verify.Addresses.CountAsync(address => address.UserId == scenario.UserId));
    }

    [SqlServerFact]
    public async Task FailureBetweenDefaultSaves_RollsBackTheClearedDefault()
    {
        var scenario = await database.SeedAsync(cart: false);
        var addresses = await SeedAddressesAsync(scenario.UserId);
        var failure = new FailDefaultAssignment(addresses[0].Id, addresses[1].Id);
        await using var factory = new SqlApiFactory(database, failure);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<InjectedFailure>(() => scope.ServiceProvider.GetRequiredService<ICustomerAccountService>()
                .SetDefaultAddressAsync(scenario.UserId, addresses[1].Id));
        }
        Assert.True(failure.ClearedDefaultSaved);
        await using var verify = database.Context();
        var defaults = await verify.Addresses.Where(address => address.UserId == scenario.UserId && address.IsDefault).ToListAsync();
        Assert.Equal(addresses[0].Id, Assert.Single(defaults).Id);
    }

    private static CreateAddressDto Address(string label) => new()
    { FullName = label, Phone = "01012345678", City = "Cairo", Street = label + " isolated street", Building = "1" };

    private static async Task<AddressResponseDto> CreateAddressAsync(HttpClient client, CreateAddressDto request)
    {
        var response = await client.PostAsJsonAsync("/api/account/addresses", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AddressResponseDto>())!;
    }

    private async Task<Address[]> SeedAddressesAsync(string userId)
    {
        var addresses = Enumerable.Range(1, 3).Select(index => new Address
        { UserId = userId, FullName = "SQL address " + index, Phone = "01012345678", City = "Cairo", Street = "SQL street", Building = "1", IsDefault = index == 1 }).ToArray();
        await using var context = database.Context(); context.Addresses.AddRange(addresses); await context.SaveChangesAsync(); return addresses;
    }

    private sealed class GatedAddresses(IAddressRepository inner, CommerceSqlServerTests.ReadGate? gate) : IAddressRepository
    {
        public async Task<IReadOnlyList<Address>> GetAllAsync(string userId, bool tracked, CancellationToken token)
        {
            var addresses = await inner.GetAllAsync(userId, tracked, token);
            if (tracked && gate is not null) await gate.ArriveAsync(token);
            return addresses;
        }
        public Task<Address?> GetByIdAsync(string userId, int id, CancellationToken token) => inner.GetByIdAsync(userId, id, token);
        public Task AddAsync(Address address, CancellationToken token) => inner.AddAsync(address, token);
        public void Delete(Address address) => inner.Delete(address);
        public Task<int> SaveChangesAsync(CancellationToken token) => inner.SaveChangesAsync(token);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token) => inner.ExecuteInTransactionAsync(action, token);
    }

    private sealed class InjectedFailure : Exception;
    private sealed class FailDefaultAssignment(int previousId, int targetId) : SaveChangesInterceptor
    {
        public bool ClearedDefaultSaved { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (ClearedDefaultSaved && data.Context!.ChangeTracker.Entries<Address>().Any(entry => entry.Entity.Id == targetId && entry.Entity.IsDefault))
                throw new InjectedFailure();
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken token = default)
        {
            if (data.Context!.ChangeTracker.Entries<Address>().Any(entry => entry.Entity.Id == previousId && !entry.Entity.IsDefault)) ClearedDefaultSaved = true;
            return ValueTask.FromResult(result);
        }
    }
}
