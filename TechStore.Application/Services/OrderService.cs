using FluentValidation;
using TechStore.Application.Common;
using TechStore.Application.DTOs.Orders;
using TechStore.Application.Exceptions;
using TechStore.Application.Interfaces.Repositories;
using TechStore.Application.Interfaces.Services;
using TechStore.Application.Mapping;
using TechStore.Domain.Entities;
using TechStore.Domain.Enums;

namespace TechStore.Application.Services;

public sealed class OrderService(IOrderRepository repository, IValidator<OrderQueryDto> queryValidator,
    IValidator<UpdateOrderStatusDto> updateValidator, IValidator<CancelOrderDto> cancelValidator) : IOrderService
{
    public async Task<PagedResult<OrderListDto>> GetCustomerOrdersAsync(string userId, OrderQueryDto query,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        await RequestValidation.ValidateAsync(queryValidator, query, cancellationToken);
        return await repository.GetAllAsync(query, userId, cancellationToken);
    }

    public async Task<PagedResult<OrderListDto>> GetAdminOrdersAsync(OrderQueryDto query, CancellationToken cancellationToken = default)
    {
        await RequestValidation.ValidateAsync(queryValidator, query, cancellationToken);
        return await repository.GetAllAsync(query, userId: null, cancellationToken);
    }

    public async Task<OrderResponseDto> GetCustomerOrderAsync(string userId, int id, CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        return (await repository.GetByIdAsync(id, userId, cancellationToken) ?? throw new NotFoundException(nameof(Order), id)).ToOrderResponse();
    }

    public async Task<OrderResponseDto> GetAdminOrderAsync(int id, CancellationToken cancellationToken = default) =>
        (await repository.GetByIdAsync(id, userId: null, cancellationToken) ?? throw new NotFoundException(nameof(Order), id)).ToOrderResponse();

    public async Task<OrderResponseDto> CancelCustomerOrderAsync(string userId, int id, CancelOrderDto dto,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(userId);
        await RequestValidation.ValidateAsync(cancelValidator, dto, cancellationToken);
        return await ChangeStatusAsync(id, userId, userId, dto.ExpectedStatus!.Value, OrderStatus.Cancelled, dto.Note,
            customerCancellation: true, cancellationToken);
    }

    public async Task<OrderResponseDto> UpdateStatusAsync(string adminId, int id, UpdateOrderStatusDto dto,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(adminId);
        await RequestValidation.ValidateAsync(updateValidator, dto, cancellationToken);
        return await ChangeStatusAsync(id, userId: null, adminId, dto.ExpectedStatus!.Value, dto.Status!.Value, dto.Note,
            customerCancellation: false, cancellationToken);
    }

    private Task<OrderResponseDto> ChangeStatusAsync(int id, string? userId, string actorId, OrderStatus expectedStatus,
        OrderStatus nextStatus, string? note, bool customerCancellation, CancellationToken cancellationToken) =>
        repository.ExecuteInTransactionAsync(async token =>
        {
            var order = await repository.GetForUpdateAsync(id, userId, token) ?? throw new NotFoundException(nameof(Order), id);
            // The first completed cancellation is final; retries never restore stock or append history again.
            if (order.Status == OrderStatus.Cancelled && nextStatus == OrderStatus.Cancelled) return order.ToOrderResponse();
            if (order.Status != expectedStatus)
                throw new ConflictException("The order status changed. Reload it before trying again.");
            if ((customerCancellation && !order.CanCustomerCancel()) || !order.CanTransitionTo(nextStatus))
                throw new ConflictException("The requested order status transition is not allowed.");

            if (nextStatus == OrderStatus.Cancelled)
                await RestoreStockAsync(order, token);
            RequestValidation.ApplyDomainRules(() => { order.TransitionTo(nextStatus, actorId, note); return order; });
            await repository.SaveChangesAsync(token);
            return order.ToOrderResponse();
        }, cancellationToken);

    private async Task RestoreStockAsync(Order order, CancellationToken cancellationToken)
    {
        var restorations = order.Items.GroupBy(item => item.ProductId)
            .Select(group => new { ProductId = group.Key, Quantity = group.Sum(item => (long)item.Quantity) }).ToArray();
        var products = (await repository.GetProductsForUpdateAsync(restorations.Select(line => line.ProductId).ToArray(), cancellationToken))
            .ToDictionary(product => product.Id);
        // Validate every line before mutating any tracked product. The transaction also
        // protects against partial persistence if the subsequent SQL save fails.
        foreach (var line in restorations)
        {
            if (!products.TryGetValue(line.ProductId, out var product) || line.Quantity <= 0
                || line.Quantity > int.MaxValue || product.Stock + line.Quantity > int.MaxValue)
                throw new ConflictException("The cancelled order's stock cannot be restored safely. Review inventory first.");
        }
        foreach (var line in restorations) products[line.ProductId].RestoreStock((int)line.Quantity);
    }

    private static void RequireIdentity(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 450) throw new UnauthorizedException();
    }
}
