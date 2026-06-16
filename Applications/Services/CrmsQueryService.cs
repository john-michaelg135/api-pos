using Applications.Interfaces;
using Api.Contracts.Crms;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class CrmsQueryService : ICrmsQueryService
{
    private readonly PosDbContext _db;

    public CrmsQueryService(PosDbContext db)
    {
        _db = db;
    }

    // ────────────────────────────────────────────────────
    // CRMS-POS-001: Get customer orders — one row per OrderItem
    // ────────────────────────────────────────────────────

    public async Task<CrmsCustomerOrdersResponseDto> GetCustomerOrdersAsync(string customerId, CrmsOrderFilterDto filter)
    {
        // CustomerId in Order is int? — try to parse the incoming string
        if (!int.TryParse(customerId, out var customerIdInt))
        {
            // Non-numeric ID: return empty result (CRMS uses int-based customer IDs in POS)
            return new CrmsCustomerOrdersResponseDto();
        }

        var query = _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Where(o => o.CustomerId == customerIdInt)
            .AsQueryable();

        // ── Apply filters ──
        if (!string.IsNullOrWhiteSpace(filter.OrderStatus))
            query = query.Where(o => o.OrderStatus == filter.OrderStatus);

        if (!string.IsNullOrWhiteSpace(filter.PaymentStatus))
            query = query.Where(o => o.PaymentStatus == filter.PaymentStatus);

        if (!string.IsNullOrWhiteSpace(filter.PaymentMethod))
            query = query.Where(o => o.PaymentMethod == filter.PaymentMethod);

        if (filter.OrderedFrom.HasValue)
            query = query.Where(o => o.CreatedAt >= filter.OrderedFrom.Value);

        if (filter.OrderedTo.HasValue)
            query = query.Where(o => o.CreatedAt <= filter.OrderedTo.Value);

        if (filter.DeliveredFrom.HasValue)
            query = query.Where(o => o.ApprovedAt >= filter.DeliveredFrom.Value);

        if (filter.DeliveredTo.HasValue)
            query = query.Where(o => o.ApprovedAt <= filter.DeliveredTo.Value);

        if (filter.OrderId.HasValue)
            query = query.Where(o => o.OrderId == filter.OrderId.Value);


        if (filter.ProductId.HasValue)
            query = query.Where(o => o.OrderItems.Any(
                oi => oi.ProductVariation.ProductId == filter.ProductId.Value));

        // ── Sorting ──
        var sortAsc = string.Equals(filter.SortOrder, "asc", StringComparison.OrdinalIgnoreCase);
        query = (filter.SortBy?.ToLower()) switch
        {
            "totalamount" => sortAsc ? query.OrderBy(o => o.TotalAmount)  : query.OrderByDescending(o => o.TotalAmount),
            "orderstatus" => sortAsc ? query.OrderBy(o => o.OrderStatus) : query.OrderByDescending(o => o.OrderStatus),
            _             => sortAsc ? query.OrderBy(o => o.CreatedAt)   : query.OrderByDescending(o => o.CreatedAt)
        };

        var orders = await query.ToListAsync();

        // ── Expand: one row per OrderItem (Option A) ──
        var rows = new List<CrmsOrderSummaryDto>();

        foreach (var order in orders)
        {
            // Compute order subtotal as sum of item subtotals
            var orderSubtotal = order.OrderItems.Sum(oi => oi.Subtotal);

            foreach (var item in order.OrderItems)
            {
                rows.Add(new CrmsOrderSummaryDto
                {
                    OrderId       = order.OrderId,
                    OrderStatus   = order.OrderStatus,
                    PaymentStatus = order.PaymentStatus,
                    PaymentMethod = order.PaymentMethod,

                    Product = new CrmsProductRefDto
                    {
                        ProductId   = item.ProductVariation.ProductId,
                        ProductName = item.ProductVariation.Product?.ProductName ?? string.Empty
                    },

                    Quantity = item.Quantity,

                    Pricing = new CrmsOrderPricingDto
                    {
                        SubtotalAmount       = orderSubtotal,
                        TotalAmount          = order.TotalAmount
                    },

                    DeliveryAddress = order.DeliveryAddress,
                    OrderedAt       = order.CreatedAt,
                    DeliveredAt     = order.ApprovedAt  // ApprovedAt = when delivery was confirmed
                });
            }
        }

        return new CrmsCustomerOrdersResponseDto { Items = rows };
    }

    // ────────────────────────────────────────────────────
    // CRMS-POS-002: Full order details with all line items
    // ────────────────────────────────────────────────────

    public async Task<CrmsOrderDetailDto?> GetOrderDetailAsync(int orderId)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderId == orderId);

        if (order == null) return null;

        var subtotal = order.OrderItems.Sum(oi => oi.Subtotal);

        return new CrmsOrderDetailDto
        {
            OrderId       = order.OrderId,
            CustomerId    = order.CustomerId?.ToString(),
            OrderStatus   = order.OrderStatus,
            PaymentStatus = order.PaymentStatus,
            PaymentMethod = order.PaymentMethod,

            Items = order.OrderItems.Select(oi => new CrmsOrderItemDto
            {
                ProductId   = oi.ProductVariation.ProductId,
                ProductName = oi.ProductVariation.Product?.ProductName ?? string.Empty,
                Quantity    = oi.Quantity,
                Price       = oi.UnitPrice
            }).ToList(),

            Pricing = new CrmsOrderPricingDto
            {
                SubtotalAmount        = subtotal,
                TotalAmount           = order.TotalAmount
            },

            DeliveryAddress = order.DeliveryAddress,
            OrderedAt       = order.CreatedAt,
            DeliveredAt     = order.ApprovedAt
        };
    }

    // ────────────────────────────────────────────────────
    // CRMS-POS-003: Customer's active cart with computed totals
    // ────────────────────────────────────────────────────

    public async Task<CrmsCartDto?> GetCustomerCartAsync(string customerId)
    {
        var cartItems = await _db.CartItems
            .Include(ci => ci.Variation)
                .ThenInclude(v => v!.Product)
            .Include(ci => ci.Variation)
                .ThenInclude(v => v!.ProductPrices.Where(p => p.IsActive))
            .Where(ci => ci.CustomerId == customerId)
            .OrderBy(ci => ci.Variation!.Product!.ProductName)
            .AsNoTracking()
            .ToListAsync();

        if (!cartItems.Any()) return null;

        var itemDtos = cartItems.Select(ci =>
        {
            // Use the active price snapshot (most recently effective active price)
            var unitPrice = ci.Variation?.ProductPrices
                .Where(p => p.IsActive)
                .OrderByDescending(p => p.EffectiveFrom)
                .FirstOrDefault()?.Price ?? 0m;

            var totalPrice = unitPrice * ci.Quantity;

            return new CrmsCartItemDto
            {
                ProductId   = ci.Variation?.ProductId ?? 0,
                ProductName = ci.Variation?.Product?.ProductName ?? string.Empty,
                Quantity    = ci.Quantity,
                UnitPrice   = unitPrice,
                TotalPrice  = totalPrice
            };
        }).ToList();

        var subtotal = itemDtos.Sum(i => i.TotalPrice);

        return new CrmsCartDto
        {
            CustomerId = customerId,
            Items      = itemDtos,
            Pricing    = new CrmsCartPricingDto
            {
                SubtotalAmount = subtotal,
                DiscountAmount = 0,     // Cart-level discount not yet implemented
                TotalAmount    = subtotal
            },
            UpdatedAt = cartItems.Max(ci => ci.UpdatedAt)
        };
    }
}
