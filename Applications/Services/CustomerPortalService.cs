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
            PaymentMethod = o.PaymentMethod ?? string.Empty,
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

    public async Task<RefundResult> RequestRefundAsync(int orderId, int customerId, string reason)
    {
        var order = await _db.Orders.Include(o => o.OrderItems).FirstOrDefaultAsync(o => o.OrderId == orderId && o.CustomerId == customerId);
        return await ProcessRefund(order, reason);
    }

    public async Task<RefundResult> RequestRefundByAuthIdAsync(int orderId, string customerAuthId, string reason)
    {
        var order = await _db.Orders.Include(o => o.OrderItems).FirstOrDefaultAsync(o => o.OrderId == orderId && o.CustomerAuthId == customerAuthId);
        return await ProcessRefund(order, reason);
    }

    private async Task<RefundResult> ProcessRefund(Order? order, string reason)
    {
        if (order == null)
            return RefundResult.Fail("Order not found.");

        if (string.IsNullOrWhiteSpace(reason))
            return RefundResult.Fail("A refund reason is required.");

        var status = order.OrderStatus.ToLower();
        bool isDelivered = status.Contains("delivered") || status.Contains("completed");
        if (!isDelivered)
            return RefundResult.Fail("Refunds can only be requested for delivered orders.");

        bool isCOD = string.Equals(order.PaymentMethod, "COD", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(order.PaymentMethod, "Cash on Delivery", StringComparison.OrdinalIgnoreCase);
        bool isOnlinePaid = !isCOD && string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);

        if (!isCOD && !isOnlinePaid)
            return RefundResult.Fail("Only paid or COD orders that have been delivered are eligible for a refund.");

        if (string.Equals(order.OrderStatus, "Refund Requested", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(order.OrderStatus, "Refunded", StringComparison.OrdinalIgnoreCase))
            return RefundResult.Fail("A refund request has already been submitted for this order.");

        order.OrderStatus = "Refund Requested";
        order.RejectionRemarks = reason;
        order.UpdatedAt = DateTime.UtcNow;
        _db.Orders.Update(order);

        foreach (var item in order.OrderItems)
        {
            var refundRequest = new RefundRequest
            {
                OrderId = order.OrderId,
                VariationId = item.VariationId,
                LocationId = order.LocationId ?? 1, // Fallback if no location
                QuantityToReturn = item.Quantity,
                Reason = reason,
                Status = "Pending",
                RequestedBy = order.CustomerId ?? 0, // Customer initiated
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _db.RefundRequests.AddAsync(refundRequest);
        }

        await _db.SaveChangesAsync();

        return RefundResult.Ok("Your refund request has been submitted successfully.");
    }

    public async Task<List<CustomerRefundRequestDto>> GetRefundRequestsAsync(int customerId)
    {
        var refunds = await _db.RefundRequests
            .AsNoTracking()
            .Include(r => r.Variation)
                .ThenInclude(v => v!.Product)
            .Join(_db.Orders,
                r => r.OrderId,
                o => o.OrderId,
                (r, o) => new { Refund = r, Order = o })
            .Where(x => x.Order.CustomerId == customerId)
            .OrderByDescending(x => x.Refund.CreatedAt)
            .ToListAsync();

        return refunds.Select(x => MapRefund(x.Refund, x.Order)).ToList();
    }

    public async Task<List<CustomerRefundRequestDto>> GetRefundRequestsByAuthIdAsync(string customerAuthId)
    {
        var refunds = await _db.RefundRequests
            .AsNoTracking()
            .Include(r => r.Variation)
                .ThenInclude(v => v!.Product)
            .Join(_db.Orders,
                r => r.OrderId,
                o => o.OrderId,
                (r, o) => new { Refund = r, Order = o })
            .Where(x => x.Order.CustomerAuthId == customerAuthId)
            .OrderByDescending(x => x.Refund.CreatedAt)
            .ToListAsync();

        return refunds.Select(x => MapRefund(x.Refund, x.Order)).ToList();
    }

    private static CustomerRefundRequestDto MapRefund(RefundRequest r, Order o) => new()
    {
        RefundRequestId = r.RefundRequestId,
        OrderId = o.OrderId,
        OrderNumber = o.OrderNumber,
        ProductName = r.Variation?.Product?.ProductName ?? "Unknown Product",
        VariationName = r.Variation?.VariationName ?? "Unknown Variation",
        QuantityToReturn = r.QuantityToReturn,
        TotalAmount = o.TotalAmount,
        PaymentMethod = o.PaymentMethod,
        Reason = r.Reason,
        Status = r.Status,
        CreatedAt = r.CreatedAt
    };
}
