using Applications.Interfaces;
using Api.Contracts.OrderEntry;
using Api.Contracts.Shared;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class OrderEntryService : IOrderEntryService
{
    private readonly PosDbContext _db;
    private readonly IInventoryService _inventoryService;

    public OrderEntryService(PosDbContext db, IInventoryService inventoryService)
    {
        _db = db;
        _inventoryService = inventoryService;
    }

    // ────────────────────────────────────────────────────
    // POS-005: Product grid for POS order screen
    // ────────────────────────────────────────────────────

    public async Task<List<ProductGridItemDto>> GetProductGridAsync(int? locationId = null)
    {
        var gridItems = await _db.ProductVariations
            .AsNoTracking()
            .Where(v => v.IsActive && v.Product.IsActive)
            .Include(v => v.Product)
            .Include(v => v.ProductPrices.Where(pp => pp.IsActive))
            .Select(v => new ProductGridItemDto
            {
                ProductId       = v.Product.ProductId,
                ProductName     = v.Product.ProductName,
                ProductImage    = v.Product.ProductImage,
                ProductCategory = v.Product.ProductCategory,
                VariationId     = v.VariationId,
                VariationName   = v.VariationName,
                Price           = v.ProductPrices
                    .Where(pp => pp.IsActive)
                    .Select(pp => pp.Price)
                    .FirstOrDefault()
            })
            .OrderBy(g => g.ProductName)
            .ThenBy(g => g.VariationName)
            .ToListAsync();

        // EC-004: overlay stock availability when locationId is provided
        if (locationId.HasValue)
        {
            var varIds = gridItems.Select(g => g.VariationId).ToList();
            var stocks = await _db.Stocks
                .AsNoTracking()
                .Where(s => s.LocationId == locationId.Value && varIds.Contains(s.VariationId))
                .ToListAsync();
            var stockMap = stocks.ToDictionary(s => s.VariationId, s => s.Quantity);
            foreach (var item in gridItems)
            {
                if (stockMap.TryGetValue(item.VariationId, out var qty))
                { item.StockQuantity = qty; item.IsInStock = qty > 0; }
                else
                { item.StockQuantity = 0;  item.IsInStock = false; }
            }
        }

        return gridItems;
    }

    // ────────────────────────────────────────────────────
    // POS-006: Order creation (with price snapshot — POS-004)
    // ────────────────────────────────────────────────────

    public async Task<OrderResponseDto> CreateOrderAsync(CreateOrderDto dto)
    {
        var now = DateTime.UtcNow;

        // Validate location exists (if provided)
        if (dto.LocationId.HasValue)
        {
            var locationExists = await _db.Locations.AnyAsync(l => l.LocationId == dto.LocationId.Value);
            if (!locationExists)
                throw new InvalidOperationException($"Location with ID {dto.LocationId.Value} not found. Create a location first.");
        }

        // Validate all variation IDs exist before creating the order
        foreach (var cartItem in dto.Items)
        {
            var variationExists = await _db.ProductVariations.AnyAsync(v => v.VariationId == cartItem.VariationId);
            if (!variationExists)
                throw new InvalidOperationException($"Product variation with ID {cartItem.VariationId} not found.");
        }

        // Generate order number: ORD-YYYYMMDD-####
        var orderNumber = await GenerateOrderNumberAsync(now);

        // Create the order
        var order = new Order
        {
            OrderNumber = orderNumber,
            OrderType = dto.OrderType,
            OrderSource = "POS",
            LocationId = dto.LocationId,
            SubmittedBy = dto.SubmittedBy,
            PaymentMethod = dto.PaymentMethod,
            PaymentStatus = "Paid",       // Walk-in POS orders are paid immediately
            OrderStatus = "Completed",    // Sprint 1: walk-in orders complete on creation
            TotalAmount = 0,              // Will be calculated below
            CreatedAt = now,
            UpdatedAt = now
        };

        await _db.Orders.AddAsync(order);
        await _db.SaveChangesAsync();


        // Create order items with price snapshots
        decimal totalAmount = 0;
        var orderItems = new List<OrderItem>();

        foreach (var cartItem in dto.Items)
        {
            // Get the active price for this variation (snapshot it!)
            var activePrice = await _db.ProductPrices
                .AsNoTracking()
                .Where(pp => pp.VariationId == cartItem.VariationId && pp.IsActive)
                .FirstOrDefaultAsync();

            if (activePrice == null)
                throw new InvalidOperationException(
                    $"No active price found for variation ID {cartItem.VariationId}");

            var baseUnitPrice = activePrice.Price;
            var finalUnitPrice = baseUnitPrice;

            // US-POS-032: Senior/PWD discount formula (Strip 12% VAT, deduct 20%)
            if (dto.ApplyPwdDiscount)
            {
                finalUnitPrice = Math.Round((baseUnitPrice / 1.12m) * 0.80m, 2);
            }

            var subtotal = finalUnitPrice * cartItem.Quantity;
            totalAmount += subtotal;

            var orderItem = new OrderItem
            {
                OrderId = order.OrderId,
                VariationId = cartItem.VariationId,
                Quantity = cartItem.Quantity,
                UnitPrice = finalUnitPrice,        // Price snapshot at checkout (POS-004)
                Subtotal = subtotal,
                CreatedAt = now
            };

            orderItems.Add(orderItem);
        }

        await _db.OrderItems.AddRangeAsync(orderItems);

        // US-POS-033: Voucher Validation and Subtotal Deduction Math
        decimal voucherDiscountAmount = 0;
        string? appliedVoucherCode = null;

        if (!string.IsNullOrWhiteSpace(dto.VoucherCode))
        {
            var voucher = await _db.Vouchers
                .Where(v => v.VoucherCode == dto.VoucherCode.Trim() && v.IsActive && v.ExpiryDate > now)
                .FirstOrDefaultAsync();

            if (voucher == null)
                throw new InvalidOperationException($"Voucher code '{dto.VoucherCode}' is invalid, inactive, or expired.");

            if (totalAmount < voucher.MinimumSpend)
                throw new InvalidOperationException($"Order subtotal must be at least {voucher.MinimumSpend:C} to apply voucher '{voucher.VoucherCode}'.");

            appliedVoucherCode = voucher.VoucherCode;
            if (voucher.DiscountType.Equals("Fixed", StringComparison.OrdinalIgnoreCase))
            {
                voucherDiscountAmount = voucher.DiscountValue;
            }
            else // Percentage
            {
                voucherDiscountAmount = Math.Round(totalAmount * (voucher.DiscountValue / 100m), 2);
            }

            // Ensure discount doesn't exceed total amount
            if (voucherDiscountAmount > totalAmount)
            {
                voucherDiscountAmount = totalAmount;
            }

            totalAmount -= voucherDiscountAmount;
        }

        // Update total amount and tracking properties
        order.TotalAmount = totalAmount;
        order.AppliedVoucherCode = appliedVoucherCode;
        order.VoucherDiscountAmount = voucherDiscountAmount > 0 ? voucherDiscountAmount : null;
        _db.Orders.Update(order);

        await _db.SaveChangesAsync();

        // Return response with full details (order was just created, so it will always exist)
        return (await BuildOrderResponse(order.OrderId))!;
    }

    // ────────────────────────────────────────────────────
    // POS-006: Order retrieval
    // ────────────────────────────────────────────────────

    public async Task<List<OrderResponseDto>> GetAllOrdersAsync()
    {
        var orders = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Location)
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        return orders.Select(MapToOrderResponse).ToList();
    }

    public async Task<OrderResponseDto?> GetOrderByIdAsync(int orderId)
    {
        return await BuildOrderResponse(orderId);
    }

    // ────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────

    private async Task<string> GenerateOrderNumberAsync(DateTime date)
    {
        var datePrefix = date.ToString("yyyyMMdd");
        var pattern = $"ORD-{datePrefix}-%";

        // Count existing orders for today to get the next sequence number
        var todayOrderCount = await _db.Orders
            .Where(o => o.OrderNumber.StartsWith($"ORD-{datePrefix}-"))
            .CountAsync();

        var sequence = (todayOrderCount + 1).ToString("D4");
        return $"ORD-{datePrefix}-{sequence}";
    }

    // ────────────────────────────────────────────────────
    // POS-007: Walk-in order confirm + optional receipt
    // ────────────────────────────────────────────────────

    public async Task<OrderResponseDto?> ConfirmOrderAsync(int orderId, ConfirmOrderDto dto)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;

        if (order.OrderStatus == "Completed")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already completed.");

        var now = DateTime.UtcNow;
        order.OrderStatus = "Completed";
        order.PaymentStatus = "Paid";
        order.ApprovedBy = dto.SubmittedBy;
        order.ApprovedAt = now;
        order.UpdatedAt = now;

        _db.Orders.Update(order);

        // POS-031: Write Payment record on confirm
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

        return await BuildOrderResponse(orderId);
    }

    // ────────────────────────────────────────────────────
    // POS-008: Institutional order creation
    // ────────────────────────────────────────────────────

    public async Task<OrderResponseDto> CreateInstitutionalOrderAsync(CreateInstitutionalOrderDto dto)
    {
        var now = DateTime.UtcNow;

        // Validate location if provided
        if (dto.LocationId.HasValue)
        {
            var locationExists = await _db.Locations.AnyAsync(l => l.LocationId == dto.LocationId.Value);
            if (!locationExists)
                throw new InvalidOperationException($"Location with ID {dto.LocationId.Value} not found.");
        }

        // Validate all variation IDs
        foreach (var cartItem in dto.Items)
        {
            var variationExists = await _db.ProductVariations.AnyAsync(v => v.VariationId == cartItem.VariationId);
            if (!variationExists)
                throw new InvalidOperationException($"Product variation with ID {cartItem.VariationId} not found.");
        }

        var orderNumber = await GenerateOrderNumberAsync(now);

        var order = new Order
        {
            OrderNumber = orderNumber,
            OrderType = "Institutional",
            OrderSource = "POS",
            LocationId = dto.LocationId,
            DeliveryAddress = dto.DeliveryAddress,
            ContactPerson = dto.ContactPerson,
            CustomVariationNotes = dto.CustomVariationNotes,
            SubmittedBy = dto.SubmittedBy,
            PaymentMethod = dto.PaymentMethod,
            PaymentStatus = "Pending",      // Institutional orders start as Pending (COD)
            OrderStatus = "Processing",     // Requires fulfillment before completion
            TotalAmount = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _db.Orders.AddAsync(order);
        await _db.SaveChangesAsync();

        decimal totalAmount = 0;
        var orderItems = new List<OrderItem>();

        foreach (var cartItem in dto.Items)
        {
            var activePrice = await _db.ProductPrices
                .Where(pp => pp.VariationId == cartItem.VariationId && pp.IsActive)
                .FirstOrDefaultAsync();

            var unitPrice = activePrice?.Price ?? 0;
            var subtotal = unitPrice * cartItem.Quantity;
            totalAmount += subtotal;

            orderItems.Add(new OrderItem
            {
                OrderId = order.OrderId,
                VariationId = cartItem.VariationId,
                Quantity = cartItem.Quantity,
                UnitPrice = unitPrice,
                Subtotal = subtotal,
                CreatedAt = now
            });
        }

        await _db.OrderItems.AddRangeAsync(orderItems);
        order.TotalAmount = totalAmount;
        order.UpdatedAt = DateTime.UtcNow;
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return (await BuildOrderResponse(order.OrderId))!;
    }

    // ────────────────────────────────────────────────────
    // POS-009: Pre-order flag toggle
    // ────────────────────────────────────────────────────

    public async Task<OrderResponseDto?> SetPreorderAsync(int orderId, bool isPreorder)
    {
        var order = await _db.Orders.FindAsync(orderId);
        if (order == null) return null;

        order.IsPreorder = isPreorder;
        order.UpdatedAt = DateTime.UtcNow;

        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return await BuildOrderResponse(orderId);
    }

    // ────────────────────────────────────────────────────
    // EC-008 / EC-019: Ecommerce order submission
    // ────────────────────────────────────────────────────

    public async Task<OrderResponseDto> CreateEcommerceOrderAsync(CreateEcommerceOrderDto dto)
    {
        var now = DateTime.UtcNow;

        foreach (var cartItem in dto.Items)
        {
            var exists = await _db.ProductVariations.AnyAsync(v => v.VariationId == cartItem.VariationId);
            if (!exists)
                throw new InvalidOperationException($"Product variation with ID {cartItem.VariationId} not found.");
        }

        var orderNumber = await GenerateOrderNumberAsync(now);

        var order = new Order
        {
            OrderNumber           = orderNumber,
            OrderType             = dto.OrderType,
            OrderSource           = "Ecommerce",
            CustomerId            = dto.CustomerId,
            DeliveryAddress       = dto.DeliveryAddress,
            InstitutionalStreet   = dto.InstitutionalStreet,
            InstitutionalCity     = dto.InstitutionalCity,
            InstitutionalProvince = dto.InstitutionalProvince,
            InstitutionalZipCode  = dto.InstitutionalZipCode,
            ContactPerson         = dto.ContactPerson,
            IsPreorder            = dto.IsPreorder,
            CustomVariationNotes  = dto.CustomVariationNotes,
            PaymentMethod         = dto.PaymentMethod,
            PaymentStatus         = "Pending",
            OrderStatus           = "Pending",
            TotalAmount           = 0,
            CreatedAt             = now,
            UpdatedAt             = now
        };

        await _db.Orders.AddAsync(order);
        await _db.SaveChangesAsync();

        decimal totalAmount = 0;
        var orderItems = new List<OrderItem>();

        foreach (var cartItem in dto.Items)
        {
            var activePrice = await _db.ProductPrices.AsNoTracking()
                .Where(pp => pp.VariationId == cartItem.VariationId && pp.IsActive)
                .FirstOrDefaultAsync();

            if (activePrice == null)
                throw new InvalidOperationException($"No active price found for variation ID {cartItem.VariationId}");

            var finalPrice = dto.ApplyPwdDiscount
                ? Math.Round((activePrice.Price / 1.12m) * 0.80m, 2)
                : activePrice.Price;

            var subtotal = finalPrice * cartItem.Quantity;
            totalAmount += subtotal;

            orderItems.Add(new OrderItem
            {
                OrderId = order.OrderId, VariationId = cartItem.VariationId,
                Quantity = cartItem.Quantity, UnitPrice = finalPrice,
                Subtotal = subtotal, CreatedAt = now
            });
        }

        await _db.OrderItems.AddRangeAsync(orderItems);

        decimal voucherDiscount = 0;
        string? appliedCode = null;

        if (!string.IsNullOrWhiteSpace(dto.VoucherCode))
        {
            var voucher = await _db.Vouchers
                .Where(v => v.VoucherCode == dto.VoucherCode.Trim() && v.IsActive && v.ExpiryDate > now)
                .FirstOrDefaultAsync();

            if (voucher == null)
                throw new InvalidOperationException($"Voucher '{dto.VoucherCode}' is invalid, inactive, or expired.");
            if (totalAmount < voucher.MinimumSpend)
                throw new InvalidOperationException($"Minimum spend of {voucher.MinimumSpend:C} required.");

            appliedCode = voucher.VoucherCode;
            voucherDiscount = voucher.DiscountType.Equals("Fixed", StringComparison.OrdinalIgnoreCase)
                ? voucher.DiscountValue
                : Math.Round(totalAmount * (voucher.DiscountValue / 100m), 2);
            if (voucherDiscount > totalAmount) voucherDiscount = totalAmount;
            totalAmount -= voucherDiscount;
        }

        order.TotalAmount           = totalAmount;
        order.AppliedVoucherCode    = appliedCode;
        order.VoucherDiscountAmount = voucherDiscount > 0 ? voucherDiscount : null;
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        return (await BuildOrderResponse(order.OrderId))!;
    }

    private async Task<OrderResponseDto?> BuildOrderResponse(int orderId)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Location)
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);

        if (order == null) return null;

        return MapToOrderResponse(order);
    }

    private static OrderResponseDto MapToOrderResponse(Order order)
    {
        return new OrderResponseDto
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            OrderType = order.OrderType,
            OrderSource = order.OrderSource,
            LocationName = order.Location?.LocationName,
            TotalAmount = order.TotalAmount,
            AppliedVoucherCode = order.AppliedVoucherCode,
            VoucherDiscountAmount = order.VoucherDiscountAmount,
            OrderStatus = order.OrderStatus,
            PaymentMethod = order.PaymentMethod,
            PaymentStatus = order.PaymentStatus,
            CreatedAt = order.CreatedAt,
            Items = order.OrderItems.Select(oi => new OrderItemResponseDto
            {
                ItemId        = oi.ItemId,
                VariationId   = oi.VariationId,
                ProductName   = oi.ProductVariation?.Product?.ProductName ?? string.Empty,
                VariationName = oi.ProductVariation?.VariationName ?? string.Empty,
                Quantity      = oi.Quantity,
                UnitPrice     = oi.UnitPrice,
                Subtotal      = oi.Subtotal
            }).ToList(),
            Payments = order.Payments.Select(p => new PaymentResponseDto
            {
                PaymentId              = p.PaymentId,
                OrderId                = p.OrderId,
                AmountPaid             = p.AmountPaid,
                PaymentChannel         = p.PaymentChannel,
                GatewayReferenceNumber = p.GatewayReferenceNumber,
                PaymentStatus          = p.PaymentStatus,
                PaidAt                 = p.PaidAt
            }).ToList()
        };
    }
}
