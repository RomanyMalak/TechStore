using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TechStore.Application.DTOs.Cart;
using TechStore.Application.DTOs.Orders;
using TechStore.Domain.Entities;
using TechStore.Application.Common;

namespace TechStore.Application.Mapping;

public static class CommerceMapping
{
    public static bool IsPurchasable(this Product product) => product.IsActive && !product.IsDeleted && product.Category.IsActive;
    public static CartResponseDto ToCartResponse(this Cart cart)
    {
        var items = cart.Items.OrderBy(item => item.ProductId).Select(item => new CartItemResponseDto
        {
            Id = item.Id, ProductId = item.ProductId, ProductName = item.Product.Name, SKU = item.Product.SKU,
            ImageUrl = item.Product.Images.OrderByDescending(image => image.IsPrimary).ThenBy(image => image.SortOrder).FirstOrDefault()?.ImageUrl,
            OriginalUnitPrice = item.Product.Price, UnitPrice = item.Product.DiscountPrice ?? item.Product.Price,
            Stock = item.Product.Stock, Quantity = item.Quantity, LineTotal = (item.Product.DiscountPrice ?? item.Product.Price) * item.Quantity,
            IsAvailable = item.Product.IsPurchasable() && item.Quantity <= item.Product.Stock
        }).ToList();
        var original = items.Sum(item => item.OriginalUnitPrice * item.Quantity);
        var subtotal = items.Sum(item => item.LineTotal);
        return new CartResponseDto { Id = cart.Id, CreatedAt = cart.CreatedAt, UpdatedAt = cart.UpdatedAt,
            Items = items, IsGuest = cart.UserId is null, SubTotal = subtotal, OriginalSubTotal = original,
            DiscountTotal = original - subtotal, TotalQuantity = items.Sum(item => item.Quantity),
            CanCheckout = items.Count > 0 && items.All(item => item.IsAvailable) };
    }

    public static CheckoutQuoteDto ToQuote(this Cart cart, CheckoutPolicy policy)
    {
        var response = cart.ToCartResponse();
        var canonical = string.Join(";", response.Items.Select(item => string.Join(":", item.ProductId, item.Quantity,
            item.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture), item.OriginalUnitPrice.ToString("0.00", CultureInfo.InvariantCulture), item.IsAvailable)));
        var version = Hash(canonical + "|" + policy.ShippingCost.ToString("0.00", CultureInfo.InvariantCulture));
        return new(response, policy.ShippingCost, 0, response.SubTotal + policy.ShippingCost,
            "الشحن العادي", new[] { new PaymentOptionDto(Domain.Enums.PaymentMethod.CashOnDelivery, "الدفع عند الاستلام") }, version);
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static OrderResponseDto ToOrderResponse(this Order order) => new()
    {
        Id = order.Id, OrderNumber = order.OrderNumber, UserId = order.UserId,
        // SQL datetime2 stores UTC values without retaining DateTime.Kind.
        OrderDate = DateTime.SpecifyKind(order.OrderDate, DateTimeKind.Utc),
        Status = order.Status, PaymentMethod = order.PaymentMethod, SubTotal = order.SubTotal, ShippingCost = order.ShippingCost,
        TaxAmount = order.TaxAmount, TotalAmount = order.TotalAmount, RecipientName = order.RecipientName,
        ShippingPhone = order.ShippingPhone, ShippingAddress = order.ShippingAddress,
        Items = order.Items.Select(item => new OrderItemResponseDto { Id = item.Id, ProductId = item.ProductId, ProductName = item.ProductName,
            SKU = item.SKU, UnitPrice = item.UnitPrice, Quantity = item.Quantity, LineTotal = item.LineTotal }).ToList(),
        StatusHistory = order.StatusHistory.OrderBy(entry => entry.ChangedAt).ThenBy(entry => entry.Id).Select(entry => new OrderStatusHistoryResponseDto
        { Id = entry.Id, OldStatus = entry.OldStatus, NewStatus = entry.NewStatus, ChangedByUserId = entry.ChangedByUserId,
            ChangedAt = DateTime.SpecifyKind(entry.ChangedAt, DateTimeKind.Utc), Note = entry.Note }).ToList()
    };
}
