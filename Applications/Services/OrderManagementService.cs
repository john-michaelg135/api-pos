using Applications.Interfaces;
using Api.Contracts.OrderManagement;
using Api.Contracts.Shared;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class OrderManagementService : IOrderManagementService
{
    private readonly PosDbContext _db;
    private readonly IInventoryService _inventoryService;

    public OrderManagementService(PosDbContext db, IInventoryService inventoryService)
    {
        _db = db;
        _inventoryService = inventoryService;
    }

    // ────────────────────────────────────────────────────
    // POS-013: Online order approval queue
    // ────────────────────────────────────────────────────

    public async Task<List<OrderManagementResponseDto>> GetPendingApprovalAsync()
    {
        var orders = await _db.Orders
            .Where(o => o.OrderSource == "Ecommerce" && o.OrderStatus == "Pending")
            .OrderBy(o => o.CreatedAt)
            .ToListAsync();

        return orders.Select(MapToResponse).ToList();
    }

    public async Task<OrderManagementResponseDto?> ApproveOrderAsync(int orderId, ApproveOrderDto dto)
    {
        var order = await _db.Orders.FindAsync(orderId);
        if (order == null) return null;

        if (order.OrderStatus != "Pending")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already {order.OrderStatus}. Only Pending orders can be approved.");

        var now = DateTime.UtcNow;
        order.OrderStatus = "Processing";
        order.ApprovedBy = dto.ApprovedBy;
        order.ApprovedAt = now;
        order.UpdatedAt = now;

        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> RejectOrderAsync(int orderId, RejectOrderDto dto)
    {
        var order = await _db.Orders.FindAsync(orderId);
        if (order == null) return null;

        if (order.OrderStatus != "Pending")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already {order.OrderStatus}. Only Pending orders can be rejected.");

        var now = DateTime.UtcNow;
        order.OrderStatus = "Cancelled";
        order.ApprovedBy = dto.RejectedBy;
        order.RejectionRemarks = dto.RejectionRemarks;
        order.UpdatedAt = now;

        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return MapToResponse(order);
    }

    // ────────────────────────────────────────────────────
    // POS-014: Unified order list with filters
    // ────────────────────────────────────────────────────

    public async Task<List<OrderManagementResponseDto>> GetAllOrdersAsync(OrderFilterDto filter)
    {
        var query = _db.Orders.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.OrderStatus))
            query = query.Where(o => o.OrderStatus == filter.OrderStatus);

        if (!string.IsNullOrWhiteSpace(filter.OrderSource))
            query = query.Where(o => o.OrderSource == filter.OrderSource);

        if (!string.IsNullOrWhiteSpace(filter.OrderType))
            query = query.Where(o => o.OrderType == filter.OrderType);

        if (filter.LocationId.HasValue)
            query = query.Where(o => o.LocationId == filter.LocationId.Value);

        if (filter.DateFrom.HasValue)
            query = query.Where(o => o.CreatedAt >= filter.DateFrom.Value);

        if (filter.DateTo.HasValue)
            query = query.Where(o => o.CreatedAt <= filter.DateTo.Value);

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        return orders.Select(MapToResponse).ToList();
    }

    // ────────────────────────────────────────────────────
    // POS-015: COD auto-mark Paid on delivery confirm
    // ────────────────────────────────────────────────────

    public async Task<OrderManagementResponseDto?> ConfirmDeliveryAsync(int orderId, int confirmedBy)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;

        if (order.PaymentMethod != "COD")
            throw new InvalidOperationException($"Only COD orders can be confirmed via delivery. This order uses {order.PaymentMethod}.");

        if (order.OrderStatus == "Completed")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already completed.");

        var now = DateTime.UtcNow;
        order.OrderStatus   = "Completed";
        order.PaymentStatus = "Paid";
        order.ApprovedBy    = confirmedBy;
        order.ApprovedAt    = now;
        order.UpdatedAt     = now;

        _db.Orders.Update(order);

        // POS-031: Write Payment record on delivery confirm
        var payment = new Payment
        {
            OrderId        = order.OrderId,
            AmountPaid     = order.TotalAmount,
            PaymentChannel = order.PaymentMethod,
            PaymentStatus  = "Success",
            PaidAt         = now
        };
        await _db.Payments.AddAsync(payment);

        await _db.SaveChangesAsync();

        // POS-011: Auto deduct stock per item at this location
        if (order.LocationId.HasValue)
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.DeductStockAsync(item.VariationId, order.LocationId.Value, item.Quantity);
                }
                catch (InvalidOperationException)
                {
                    // Stock may not be tracked for this item/location — log and continue
                }
            }
        }

        return MapToResponse(order);
    }

    // ────────────────────────────────────────────────────
    // EC-012: Order progress tracking for progress bar
    // ────────────────────────────────────────────────────

    public async Task<OrderTrackingDto?> GetOrderTrackingAsync(int orderId)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderId == orderId);

        if (order == null) return null;

        return new OrderTrackingDto
        {
            OrderId       = order.OrderId,
            OrderNumber   = order.OrderNumber,
            OrderSource   = order.OrderSource,
            OrderType     = order.OrderType,
            OrderStatus   = order.OrderStatus,
            PaymentMethod = order.PaymentMethod,
            PaymentStatus = order.PaymentStatus,
            IsPreorder    = order.IsPreorder,
            CreatedAt     = order.CreatedAt,
            UpdatedAt     = order.UpdatedAt,
            ApprovedAt    = order.ApprovedAt
        };
    }

    // ────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────

    private static OrderManagementResponseDto MapToResponse(Order order)
    {
        return new OrderManagementResponseDto
        {
            OrderId               = order.OrderId,
            OrderNumber           = order.OrderNumber,
            OrderType             = order.OrderType,
            OrderSource           = order.OrderSource,
            OrderStatus           = order.OrderStatus,
            PaymentMethod         = order.PaymentMethod,
            PaymentStatus         = order.PaymentStatus,
            TotalAmount           = order.TotalAmount,
            AppliedVoucherCode    = order.AppliedVoucherCode,
            VoucherDiscountAmount = order.VoucherDiscountAmount,
            CustomerId            = order.CustomerId,
            LocationId            = order.LocationId,
            DeliveryAddress       = order.DeliveryAddress,
            InstitutionalStreet   = order.InstitutionalStreet,
            InstitutionalCity     = order.InstitutionalCity,
            InstitutionalProvince = order.InstitutionalProvince,
            InstitutionalZipCode  = order.InstitutionalZipCode,
            ContactPerson         = order.ContactPerson,
            IsPreorder            = order.IsPreorder,
            CustomVariationNotes  = order.CustomVariationNotes,
            SubmittedBy           = order.SubmittedBy,
            ApprovedBy            = order.ApprovedBy,
            ApprovedAt            = order.ApprovedAt,
            RejectionRemarks      = order.RejectionRemarks,
            CreatedAt             = order.CreatedAt,
            UpdatedAt             = order.UpdatedAt
        };
    }
}
