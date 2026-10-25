using Applications.Interfaces;
using Api.Contracts.OrderManagement;
using Api.Contracts.OrderEntry;
using Api.Contracts.Shared;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Api.Middlewares;

namespace Applications.Services;

public class OrderManagementService : IOrderManagementService
{
    private readonly PosDbContext _db;
    private readonly IInventoryService _inventoryService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuditLogService _auditLogService;

    public OrderManagementService(PosDbContext db, IInventoryService inventoryService, IHttpContextAccessor httpContextAccessor, IAuditLogService auditLogService)
    {
        _db = db;
        _inventoryService = inventoryService;
        _httpContextAccessor = httpContextAccessor;
        _auditLogService = auditLogService;
    }

    // ────────────────────────────────────────────────────
    // POS-013: Online order approval queue
    // ────────────────────────────────────────────────────

    public async Task<List<OrderManagementResponseDto>> GetPendingApprovalAsync()
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();

        var query = _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .Where(o => o.OrderSource == "Ecommerce" && (o.OrderStatus == "Pending" || o.OrderStatus == "Awaiting Stock"))
            .AsQueryable();

        if (currentUser != null && !PosRoleAuthorization.IsAdministrator(currentUser))
        {
            var allowedLocations = currentUser.LocationIds;
            query = query.Where(order =>
                order.LocationId.HasValue && allowedLocations.Contains(order.LocationId.Value));
        }

        var orders = await query.OrderBy(o => o.CreatedAt).ToListAsync();

        return orders.Select(MapToResponse).ToList();
    }

    public async Task<OrderManagementResponseDto?> ApproveOrderAsync(int orderId, ApproveOrderDto dto)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if ((currentUser?.SubRole == "Cashier" || currentUser?.SubRole == "OrderManager") && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId)
            throw new InvalidOperationException("You are not authorized to approve this order for another location.");
        
        if (currentUser?.SubRole == "Cashier" && order.OrderSource == "Ecommerce")
            throw new InvalidOperationException("Cashiers are not authorized to approve ecommerce orders.");

        if (order.OrderStatus != "Pending" && order.OrderStatus != "Awaiting Stock")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already {order.OrderStatus}. Only Pending or Awaiting Stock orders can be approved.");

        var currentStatus = order.OrderStatus;
        var now = DateTime.UtcNow;
        order.OrderStatus = "Processing";
        order.ApprovedBy = dto.ApprovedBy;
        order.ApprovedAt = now;
        order.UpdatedAt = now;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Processing", dto.ApprovedBy, "Order approved");
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        // Deduct stock for pre-orders upon transitioning to "Processing"
        if (order.IsPreorder && order.LocationId.HasValue)
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.DeductStockAsync(item.VariationId, order.LocationId.Value, item.Quantity);
                }
                catch (InvalidOperationException ex)
                {
                    System.Console.WriteLine($"ApproveOrderAsync: Stock deduction skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                }
            }
        }

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> RejectOrderAsync(int orderId, RejectOrderDto dto)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if ((currentUser?.SubRole == "Cashier" || currentUser?.SubRole == "OrderManager") && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId)
            throw new InvalidOperationException("You are not authorized to reject this order for another location.");

        if (currentUser?.SubRole == "Cashier" && order.OrderSource == "Ecommerce")
            throw new InvalidOperationException("Cashiers are not authorized to reject ecommerce orders.");

        if (order.OrderStatus != "Pending" && order.OrderStatus != "Awaiting Stock")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already {order.OrderStatus}. Only Pending or Awaiting Stock orders can be rejected.");

        // Restore reserved stock back to location (e.g. Commissary 999) on cancellation/rejection, ONLY if it was previously deducted
        bool shouldRestoreStock = order.LocationId.HasValue && (!order.IsPreorder || (order.IsPreorder && order.OrderStatus != "Awaiting Stock"));

        var currentStatus = order.OrderStatus;
        var now = DateTime.UtcNow;
        order.OrderStatus = "Cancelled";
        order.ApprovedBy = dto.RejectedBy;
        order.RejectionRemarks = dto.RejectionRemarks;
        order.UpdatedAt = now;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Cancelled", dto.RejectedBy, $"Order rejected: {dto.RejectionRemarks}");
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        if (shouldRestoreStock)
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity);
                }
                catch (InvalidOperationException ex)
                {
                    // Stock record may not be initialized/tracked — log it
                    System.Console.WriteLine($"RejectOrderAsync: Stock restoration skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                }
            }
        }

        return MapToResponse(order);
    }

    // ────────────────────────────────────────────────────
    // POS-014: Unified order list with filters
    // ────────────────────────────────────────────────────

    public async Task<List<OrderManagementResponseDto>> GetAllOrdersAsync(OrderFilterDto filter)
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        var query = _db.Orders.AsQueryable();

        if (currentUser is { IsSuperUser: false })
        {
            var allowedLocations = currentUser.LocationIds;
            if (allowedLocations.Length == 0 ||
                (filter.LocationId.HasValue && !allowedLocations.Contains(filter.LocationId.Value)))
            {
                return [];
            }

            query = query.Where(order =>
                order.LocationId.HasValue && allowedLocations.Contains(order.LocationId.Value));
            if (filter.LocationId.HasValue)
            {
                query = query.Where(order => order.LocationId == filter.LocationId.Value);
            }
        }
        else if (filter.LocationId.HasValue && filter.LocationId.Value > 0)
            query = query.Where(order => order.LocationId == filter.LocationId.Value);

        if (!string.IsNullOrWhiteSpace(filter.OrderStatus))
            query = query.Where(o => o.OrderStatus == filter.OrderStatus);

        if (!string.IsNullOrWhiteSpace(filter.OrderSource))
            query = query.Where(o => o.OrderSource == filter.OrderSource);

        if (!string.IsNullOrWhiteSpace(filter.OrderType))
            query = query.Where(o => o.OrderType == filter.OrderType);

        // Npgsql maps CreatedAt to `timestamp with time zone` and REQUIRES UTC
        // DateTimes for comparisons. A filter value with Unspecified/Local kind
        // would throw at query time — coerce to UTC to be safe.
        if (filter.DateFrom.HasValue)
        {
            var from = DateTime.SpecifyKind(filter.DateFrom.Value, DateTimeKind.Utc);
            query = query.Where(o => o.CreatedAt >= from);
        }

        if (filter.DateTo.HasValue)
        {
            var to = DateTime.SpecifyKind(filter.DateTo.Value, DateTimeKind.Utc);
            query = query.Where(o => o.CreatedAt <= to);
        }

        var orders = await query
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
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
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if ((currentUser?.SubRole == "Cashier" || currentUser?.SubRole == "OrderManager") && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId)
            throw new InvalidOperationException("You are not authorized to perform this action for other locations.");
            
        if (currentUser?.SubRole == "Cashier" && order.OrderSource == "Ecommerce")
            throw new InvalidOperationException("Cashiers are not authorized to confirm delivery for ecommerce orders.");

        if (order.PaymentMethod != "COD")
            throw new InvalidOperationException($"Only COD orders can be confirmed via delivery. This order uses {order.PaymentMethod}.");

        if (order.OrderStatus == "Completed")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already completed.");

        var currentStatus = order.OrderStatus;
        var now = DateTime.UtcNow;
        order.OrderStatus   = "Completed";
        order.PaymentStatus = "Paid";
        order.ApprovedBy    = confirmedBy;
        order.ApprovedAt    = now;
        order.UpdatedAt     = now;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Completed", confirmedBy, "COD delivery confirmed");
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

        // POS-011: Auto deduct stock per item at this location (only if not already deducted for Ecommerce orders at checkout)
        if (order.LocationId.HasValue && !string.Equals(order.OrderSource, "Ecommerce", StringComparison.OrdinalIgnoreCase))
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

    public async Task<OrderManagementResponseDto?> UpdateOrderStatusAsync(int orderId, UpdateOrderStatusDto dto)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser != null && !PosRoleAuthorization.IsManager(currentUser))
        {
            throw new InvalidOperationException("Only Order Managers and Admins are authorized to update the status of ecommerce orders.");
        }

        var targetStatus = dto.Status.Trim();
        var currentStatus = order.OrderStatus;

        if (string.Equals(targetStatus, currentStatus, StringComparison.OrdinalIgnoreCase))
        {
            return MapToResponse(order);
        }

        var allowedStatuses = new[] { "Pending", "Awaiting Stock", "Processing", "Shipped", "Delivered", "Completed", "Refund Requested", "Refunded", "Cancelled" };
        if (!allowedStatuses.Contains(targetStatus, System.StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid target status '{targetStatus}'.");
        }

        var now = DateTime.UtcNow;
        order.OrderStatus = targetStatus;
        order.ApprovedBy = dto.UpdatedBy;
        order.UpdatedAt = now;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, targetStatus, dto.UpdatedBy, $"Status updated via panel");

        if (string.Equals(targetStatus, "Processing", StringComparison.OrdinalIgnoreCase))
        {
            order.ApprovedAt = now;
            // Deduct stock for pre-orders upon transitioning to "Processing"
            if (order.IsPreorder && order.LocationId.HasValue)
            {
                foreach (var item in order.OrderItems)
                {
                    try
                    {
                        await _inventoryService.DeductStockAsync(item.VariationId, order.LocationId.Value, item.Quantity);
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Console.WriteLine($"UpdateOrderStatusAsync: Stock deduction skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                    }
                }
            }
        }
        else if (string.Equals(targetStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            // Restore reserved stock if cancelled, ONLY if it was previously deducted
            bool shouldRestoreStock = order.LocationId.HasValue && (!order.IsPreorder || (order.IsPreorder && currentStatus != "Awaiting Stock"));
            if (shouldRestoreStock)
            {
                foreach (var item in order.OrderItems)
                {
                    try
                    {
                        await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity);
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Console.WriteLine($"UpdateOrderStatusAsync: Stock restoration skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                    }
                }
            }
        }
        else if (string.Equals(targetStatus, "Refunded", StringComparison.OrdinalIgnoreCase))
        {
            // Restore reserved stock if refunded directly
            if (order.LocationId.HasValue)
            {
                foreach (var item in order.OrderItems)
                {
                    try
                    {
                        await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity);
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Console.WriteLine($"UpdateOrderStatusAsync: Stock restoration skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                    }
                }
            }
        }
        else if (string.Equals(targetStatus, "Completed", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(targetStatus, "Delivered", StringComparison.OrdinalIgnoreCase))
        {
            order.PaymentStatus = "Paid";
            
            var hasPayment = await _db.Payments.AnyAsync(p => p.OrderId == order.OrderId && p.PaymentStatus == "Success");
            if (!hasPayment)
            {
                var payment = new Payment
                {
                    OrderId        = order.OrderId,
                    AmountPaid     = order.TotalAmount,
                    PaymentChannel = order.PaymentMethod ?? "COD",
                    PaymentStatus  = "Success",
                    PaidAt         = now
                };
                await _db.Payments.AddAsync(payment);
            }
        }

        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

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
        EnsureCurrentUserCanAccessOrderLocation(order);

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
    // POS-016 & 017: Refunds
    // ────────────────────────────────────────────────────

    public async Task<OrderManagementResponseDto?> RequestRefundAsync(int orderId, string reason)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus != "Completed" && order.PaymentStatus != "Paid")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus} (Payment: {order.PaymentStatus}). Only Paid or Completed orders can be refunded.");

        var currentStatus = order.OrderStatus;
        order.OrderStatus = "Refund Requested";
        order.RejectionRemarks = reason; // Store reason here as agreed
        order.UpdatedAt = DateTime.UtcNow;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Refund Requested", 1, reason);
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> ApproveRefundAsync(int orderId, int approvedBy, string? pinCode)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
            
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus != "Refund Requested")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Only Refund Requested orders can be approved.");

        if (string.IsNullOrWhiteSpace(pinCode))
            throw new InvalidOperationException("Manager PIN is required to approve a refund.");

        var authUserId = currentUser?.PortalUserId ?? (currentUser?.Id != Guid.Empty ? currentUser?.Id.ToString() : null);
        if (!string.IsNullOrEmpty(authUserId))
        {
            var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
            if (userPin == null || userPin.PinCode != pinCode)
                throw new InvalidOperationException("Invalid Manager PIN.");
        }
        else
        {
            throw new InvalidOperationException("Unable to identify manager to validate PIN.");
        }

        var currentStatus = order.OrderStatus;
        order.OrderStatus = "Refunded";
        order.ApprovedBy = approvedBy;
        order.ApprovedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Refunded", approvedBy, "Refund approved");
        _db.Orders.Update(order);

        var refundRequests = await _db.RefundRequests.Where(r => r.OrderId == orderId).ToListAsync();
        foreach (var req in refundRequests)
        {
            req.Status = "Approved";
            req.ApprovedBy = approvedBy;
            req.ApprovedAt = DateTime.UtcNow;
            req.UpdatedAt = DateTime.UtcNow;
            _db.RefundRequests.Update(req);
        }

        await _db.SaveChangesAsync();

        // POS-017: Auto restore stock per item
        if (order.LocationId.HasValue)
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity);
                }
                catch (InvalidOperationException)
                {
                    // Stock may not be tracked for this item/location
                }
            }
        }

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> RejectRefundAsync(int orderId, int rejectedBy, string reason, string? pinCode)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus != "Refund Requested")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Only Refund Requested orders can be rejected.");

        if (string.IsNullOrWhiteSpace(pinCode))
            throw new InvalidOperationException("Manager PIN is required to reject a refund.");

        var authUserId = currentUser?.PortalUserId ?? (currentUser?.Id != Guid.Empty ? currentUser?.Id.ToString() : null);
        if (!string.IsNullOrEmpty(authUserId))
        {
            var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
            if (userPin == null || userPin.PinCode != pinCode)
                throw new InvalidOperationException("Invalid Manager PIN.");
        }
        else
        {
            throw new InvalidOperationException("Unable to identify manager to validate PIN.");
        }

        // Revert to Completed if refund is rejected
        var currentStatus = order.OrderStatus;
        order.OrderStatus = "Completed";
        order.ApprovedBy = rejectedBy; // Log manager id
        order.RejectionRemarks = $"Refund Rejected: {reason}";
        order.UpdatedAt = DateTime.UtcNow;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Completed", rejectedBy, $"Refund rejected: {reason}");
        _db.Orders.Update(order);

        var refundRequests = await _db.RefundRequests.Where(r => r.OrderId == orderId).ToListAsync();
        foreach (var req in refundRequests)
        {
            req.Status = "Rejected";
            req.ApprovedBy = rejectedBy; 
            req.UpdatedAt = DateTime.UtcNow;
            _db.RefundRequests.Update(req);
        }

        await _db.SaveChangesAsync();

        return MapToResponse(order);
    }

    // ────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────

    public async Task<OrderManagementResponseDto?> RequestVoidAsync(int orderId, string reason)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus == "Void Requested" || order.OrderStatus == "Voided" || order.OrderStatus == "Cancelled" || order.OrderStatus == "Refund Requested" || order.OrderStatus == "Refunded")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Cannot request void.");

        var currentStatus = order.OrderStatus;
        order.OrderStatus = "Void Requested";
        order.RejectionRemarks = reason;
        order.UpdatedAt = DateTime.UtcNow;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Void Requested", 1, reason);
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> ApproveVoidAsync(int orderId, int approvedBy, string? pinCode)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
            
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus != "Void Requested")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Only Void Requested orders can be approved.");

        if (string.IsNullOrWhiteSpace(pinCode))
            throw new InvalidOperationException("Manager PIN is required to approve a void.");

        var authUserId = currentUser?.PortalUserId ?? (currentUser?.Id != Guid.Empty ? currentUser?.Id.ToString() : null);
        if (!string.IsNullOrEmpty(authUserId))
        {
            var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
            if (userPin == null || userPin.PinCode != pinCode)
                throw new InvalidOperationException("Invalid Manager PIN.");
        }
        else
        {
            throw new InvalidOperationException("Unable to identify manager to validate PIN.");
        }

        var currentStatus = order.OrderStatus;
        order.OrderStatus = "Voided";
        order.ApprovedBy = approvedBy;
        order.ApprovedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Voided", approvedBy, "Void approved");
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        if (order.LocationId.HasValue)
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity);
                }
                catch (InvalidOperationException) {}
            }
        }

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> RejectVoidAsync(int orderId, int rejectedBy, string reason, string pinCode)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus != "Void Requested")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Only Void Requested orders can be rejected.");

        if (string.IsNullOrWhiteSpace(pinCode))
            throw new InvalidOperationException("Manager PIN is required to reject a void.");

        var authUserId = currentUser?.PortalUserId ?? (currentUser?.Id != Guid.Empty ? currentUser?.Id.ToString() : null);
        if (!string.IsNullOrEmpty(authUserId))
        {
            var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
            if (userPin == null || userPin.PinCode != pinCode)
                throw new InvalidOperationException("Invalid Manager PIN.");
        }
        else
        {
            throw new InvalidOperationException("Unable to identify manager to validate PIN.");
        }

        var currentStatus = order.OrderStatus;
        
        var previousStatusRecord = await _db.OrderStatusHistories
            .Where(h => h.OrderId == orderId && h.NewStatus == "Void Requested")
            .OrderByDescending(h => h.CreatedAt)
            .FirstOrDefaultAsync();
            
        string revertStatus = previousStatusRecord?.OldStatus ?? "Completed";
        
        order.OrderStatus = revertStatus;
        order.ApprovedBy = rejectedBy; 
        order.RejectionRemarks = $"Void Rejected: {reason}";
        order.UpdatedAt = DateTime.UtcNow;

        await RecordStatusHistoryAsync(order.OrderId, currentStatus, revertStatus, rejectedBy, $"Void rejected: {reason}");
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return MapToResponse(order);
    }

    private async Task RecordStatusHistoryAsync(int orderId, string oldStatus, string newStatus, int? changedBy, string? remarks = null)
    {
        var history = new OrderStatusHistory
        {
            OrderId = orderId,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            ChangedBy = changedBy,
            Remarks = remarks,
            CreatedAt = DateTime.UtcNow
        };
        await _db.OrderStatusHistories.AddAsync(history);
    }

    private void EnsureCurrentUserCanAccessOrderLocation(Order order)
    {
        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (user is null || PosRoleAuthorization.IsAdministrator(user))
        {
            return;
        }

        if (!order.LocationId.HasValue || !user.LocationIds.Contains(order.LocationId.Value))
        {
            throw new InvalidOperationException("You are not authorized to access orders outside your assigned location.");
        }
    }

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
            AmountTendered        = order.AmountTendered,
            ChangeAmount          = order.ChangeAmount,
            CustomerId            = order.CustomerId,
            LocationId            = order.LocationId,
            LocationName          = order.Location?.LocationName,
            DeliveryAddress       = order.DeliveryAddress,
            InstitutionalStreet   = order.InstitutionalStreet,
            InstitutionalCity     = order.InstitutionalCity,
            InstitutionalProvince = order.InstitutionalProvince,
            InstitutionalZipCode  = order.InstitutionalZipCode,
            ContactPerson         = order.ContactPerson,
            IsPreorder            = order.IsPreorder,
            CustomVariationNotes  = order.CustomVariationNotes,
            SeniorPwdId           = order.SeniorPwdId,
            SeniorPwdName         = order.SeniorPwdName,
            SeniorPwdStreet       = order.SeniorPwdStreet,
            SeniorPwdBarangay     = order.SeniorPwdBarangay,
            SeniorPwdCity         = order.SeniorPwdCity,
            SeniorPwdProvince     = order.SeniorPwdProvince,
            SeniorPwdZipCode      = order.SeniorPwdZipCode,
            SubmittedBy           = order.SubmittedBy,
            ApprovedBy            = order.ApprovedBy,
            ApprovedAt            = order.ApprovedAt,
            RejectionRemarks      = order.RejectionRemarks,
            CreatedAt             = order.CreatedAt,
            UpdatedAt             = order.UpdatedAt,
            Payments = order.Payments?.Select(p => new PaymentResponseDto
            {
                PaymentId              = p.PaymentId,
                OrderId                = p.OrderId,
                AmountPaid             = p.AmountPaid,
                PaymentChannel         = p.PaymentChannel,
                GatewayReferenceNumber = p.GatewayReferenceNumber,
                PaymentStatus          = p.PaymentStatus,
                PaidAt                 = p.PaidAt
            }).ToList() ?? new(),
            Items = order.OrderItems?.Select(oi => new OrderItemResponseDto
            {
                ItemId        = oi.ItemId,
                VariationId   = oi.VariationId,
                ProductName   = oi.ProductVariation?.Product?.ProductName ?? string.Empty,
                VariationName = oi.ProductVariation?.VariationName ?? string.Empty,
                Quantity      = oi.Quantity,
                UnitPrice     = oi.UnitPrice,
                Subtotal      = oi.Subtotal
            }).ToList() ?? new(),
            StatusHistory = order.StatusHistory?.Select(sh => new OrderStatusHistoryResponseDto
            {
                Id = sh.Id,
                OrderId = sh.OrderId,
                OldStatus = sh.OldStatus,
                NewStatus = sh.NewStatus,
                ChangedBy = sh.ChangedBy,
                Remarks = sh.Remarks,
                CreatedAt = sh.CreatedAt
            }).OrderBy(sh => sh.CreatedAt).ToList() ?? new()
        };
    }
}
