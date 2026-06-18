using Api.Contracts.Customer;
using Applications.Interfaces;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class CustomerPortalService : ICustomerPortalService
{
    private readonly PosDbContext _db;

    public CustomerPortalService(PosDbContext db)
    {
        _db = db;
    }

    public async Task<List<CustomerOrderHistoryDto>> GetOrderHistoryAsync(int customerId)
    {
        var orders = await _db.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Where(o => o.CustomerId == customerId && (o.OrderSource == "E-Commerce" || o.OrderSource == "Ecommerce"))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        return MapOrders(orders);
    }

    public async Task<List<CustomerOrderHistoryDto>> GetOrderHistoryByAuthIdAsync(string customerAuthId)
    {
        var orders = await _db.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Where(o => o.CustomerAuthId == customerAuthId && (o.OrderSource == "E-Commerce" || o.OrderSource == "Ecommerce"))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        return MapOrders(orders);
    }

    private List<CustomerOrderHistoryDto> MapOrders(List<Order> orders)
    {
        return orders.Select(o => new CustomerOrderHistoryDto
        {
            OrderId = o.OrderId,
            OrderNumber = o.OrderNumber,
            CreatedAt = o.CreatedAt,
            OrderStatus = o.OrderStatus,
            PaymentStatus = o.PaymentStatus,
            TotalAmount = o.TotalAmount,
            Items = o.OrderItems.Select(oi => new CustomerOrderHistoryItemDto
            {
                ProductName = oi.ProductVariation?.Product?.ProductName ?? "Unknown Product",
                VariationName = oi.ProductVariation?.VariationName ?? "Unknown Variation",
                Quantity = oi.Quantity,
                UnitPrice = oi.UnitPrice,
                Subtotal = oi.Subtotal
            }).ToList()
        }).ToList();
    }

    public async Task<OrderTrackingDto?> GetOrderTrackingAsync(int orderId, int customerId)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderId == orderId && o.CustomerId == customerId);

        return order == null ? null : BuildTrackingDto(order);
    }

    public async Task<OrderTrackingDto?> GetOrderTrackingByAuthIdAsync(int orderId, string customerAuthId)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderId == orderId && o.CustomerAuthId == customerAuthId);

        return order == null ? null : BuildTrackingDto(order);
    }

    private static OrderTrackingDto BuildTrackingDto(Order order)
    {
        int stage = 1;
        var status = order.OrderStatus.ToLower();

        if (status.Contains("processing") || status.Contains("preparing"))
            stage = 2;
        else if (status.Contains("shipped") || status.Contains("transit") || status.Contains("out for delivery"))
            stage = 3;
        else if (status.Contains("delivered") || status.Contains("completed"))
            stage = 4;

        return new OrderTrackingDto
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            CurrentStatus = order.OrderStatus,
            TrackingStage = stage,
            LastUpdatedAt = order.UpdatedAt
        };
    }
}
