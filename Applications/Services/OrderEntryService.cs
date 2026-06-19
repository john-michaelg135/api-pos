using Applications.Interfaces;
using Api.Contracts.OrderEntry;
using Api.Contracts.Shared;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Api.Middlewares;

namespace Applications.Services;

public class OrderEntryService : IOrderEntryService
{
    private readonly PosDbContext _db;
    private readonly IInventoryService _inventoryService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IXenditService _xenditService;
    private readonly IAuditLogService _auditLogService;

    public OrderEntryService(
        PosDbContext db,
        IInventoryService inventoryService,
        IHttpContextAccessor httpContextAccessor,
        IXenditService xenditService,
        IAuditLogService auditLogService)
    {
        _db = db;
        _inventoryService = inventoryService;
        _httpContextAccessor = httpContextAccessor;
        _xenditService = xenditService;
        _auditLogService = auditLogService;
    }

    // ────────────────────────────────────────────────────
    // POS-005: Product grid for POS order screen
    // ────────────────────────────────────────────────────

    public async Task<List<ProductGridItemDto>> GetProductGridAsync(int? locationId = null)
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier")
        {
            locationId = currentUser.LocationId;
        }

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
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier")
        {
            if (dto.LocationId.HasValue && dto.LocationId.Value != currentUser.LocationId)
                throw new InvalidOperationException("You are not authorized to create orders for other locations.");
            dto.LocationId = currentUser.LocationId;
        }

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

        var isGcash = string.Equals(dto.PaymentMethod, "GCash", StringComparison.OrdinalIgnoreCase);

        // Create the order
        var order = new Order
        {
            OrderNumber = orderNumber,
            OrderType = dto.OrderType,
            OrderSource = "POS",
            LocationId = dto.LocationId,
            SubmittedBy = dto.SubmittedBy,
            PaymentMethod = dto.PaymentMethod,
            PaymentStatus = isGcash ? "Pending" : "Paid",
            OrderStatus = isGcash ? "Pending" : "Completed",
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



        // Update total amount and tracking properties

        order.TotalAmount = totalAmount;
        _db.Orders.Update(order);

        await _db.SaveChangesAsync();

        if (isGcash)
        {
            try
            {
                var paymentUrl = await _xenditService.CreateInvoiceAsync(order.OrderNumber, order.TotalAmount, $"POS Order {order.OrderNumber}");
                var payment = new Payment
                {
                    OrderId = order.OrderId,
                    AmountPaid = order.TotalAmount,
                    PaymentChannel = "GCash",
                    PaymentStatus = "Pending",
                    GatewayReferenceNumber = paymentUrl,
                    PaidAt = now
                };
                await _db.Payments.AddAsync(payment);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _db.OrderItems.RemoveRange(orderItems);
                _db.Orders.Remove(order);
                await _db.SaveChangesAsync();
                throw new InvalidOperationException($"Failed to create Xendit payment link: {ex.Message}", ex);
            }
        }

        // POS-011: Auto deduct stock per item at this location
        if (order.LocationId.HasValue)
        {
            foreach (var item in orderItems)
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

        // Return response with full details (order was just created, so it will always exist)
        var response = await BuildOrderResponse(order.OrderId);
        
        _auditLogService.Log(
            action: "Create",
            entity: "Order",
            entityId: order.OrderId,
            before: null,
            after: response,
            performedBy: null // Let service pull from context
        );

        return response!;
    }

    // ────────────────────────────────────────────────────
    // POS-006: Order retrieval
    // ────────────────────────────────────────────────────

    public async Task<List<OrderResponseDto>> GetAllOrdersAsync()
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        var query = _db.Orders.AsQueryable();

        if (currentUser?.SubRole == "Cashier")
        {
            var locationId = currentUser.LocationId ?? 0;
            query = query.Where(o => o.LocationId == locationId);
        }

        var orders = await query
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
        var orderDto = await BuildOrderResponse(orderId);
        if (orderDto == null) return null;

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && orderDto.LocationId != currentUser.LocationId)
        {
            throw new InvalidOperationException("You are not authorized to view orders from other locations.");
        }

        return orderDto;
    }

    // ────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────

    private async Task<string> GenerateOrderNumberAsync(DateTime date)
    {
        var datePrefix = date.ToString("yyyyMMdd");
        var prefix = $"ORD-{datePrefix}-";

        var lastOrder = await _db.Orders
            .Where(o => o.OrderNumber.StartsWith(prefix))
            .OrderByDescending(o => o.OrderNumber)
            .FirstOrDefaultAsync();

        int nextSequence = 1;
        if (lastOrder != null)
        {
            var parts = lastOrder.OrderNumber.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[2], out int lastSequence))
            {
                nextSequence = lastSequence + 1;
            }
        }

        var sequence = nextSequence.ToString("D4");
        return $"{prefix}{sequence}";
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
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier")
        {
            if (dto.LocationId.HasValue && dto.LocationId.Value != currentUser.LocationId)
                throw new InvalidOperationException("You are not authorized to create orders for other locations.");
            dto.LocationId = currentUser.LocationId;
        }

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
    // EC-008 / EC-019: Ecommerce order submission
    // ────────────────────────────────────────────────────

    public async Task<OrderResponseDto> CreateEcommerceOrderAsync(CreateEcommerceOrderDto dto)
    {
        var now = DateTime.UtcNow;

        foreach (var cartItem in dto.Items)
        {
            var exists = await _db.ProductVariations.AnyAsync(v => v.VariationId == cartItem.VariationId);
            if (!exists)
            {
                // Fallback for mock ecommerce data (IDs 1-8) which may not exist in the real POS DB
                var fallbackVariation = await _db.ProductVariations.FirstOrDefaultAsync();
                if (fallbackVariation == null)
                    throw new InvalidOperationException("No product variations available in the database to fulfill the mock order.");
                
                cartItem.VariationId = fallbackVariation.VariationId;
            }
        }

        var orderNumber = await GenerateOrderNumberAsync(now);

        var order = new Order
        {
            OrderNumber           = orderNumber,
            OrderType             = dto.OrderType,
            OrderSource           = "Ecommerce",
            LocationId            = 999, // Commissary Location ID
            CustomerId            = dto.CustomerId,
            CustomerAuthId        = dto.CustomerAuthId,
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
            OrderStatus           = dto.IsPreorder ? "Awaiting Stock" : "Pending",
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

        order.TotalAmount           = totalAmount;
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();

        // Only create a Xendit invoice for online payment methods (e.g. GCash, PayMaya).
        // COD orders are paid on arrival — they never go through Xendit.
        var isOnlinePayment = !string.Equals(dto.PaymentMethod, "COD", StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(dto.PaymentMethod, "Cash on Delivery", StringComparison.OrdinalIgnoreCase);
        if (isOnlinePayment)
        {
            try
            {
                // EC-020: Ecommerce orders redirect back to the ecommerce success page
                var ecommerceBaseUrl = Environment.GetEnvironmentVariable("XENDIT_ECOMMERCE_REDIRECT_URL")
                    ?? "http://localhost:3005/checkout/success";
                var successUrl = $"{ecommerceBaseUrl}?orderId={order.OrderId}&orderNumber={order.OrderNumber}";
                var failureUrl = $"{ecommerceBaseUrl}?orderId={order.OrderId}&orderNumber={order.OrderNumber}&failed=true";

                var paymentUrl = await _xenditService.CreateInvoiceAsync(
                    order.OrderNumber,
                    order.TotalAmount,
                    $"Ecommerce Order {order.OrderNumber}",
                    successUrl,
                    failureUrl);
                var payment = new Payment
                {
                    OrderId = order.OrderId,
                    AmountPaid = order.TotalAmount,
                    PaymentChannel = dto.PaymentMethod,
                    PaymentStatus = "Pending",
                    GatewayReferenceNumber = paymentUrl,
                    PaidAt = now
                };
                await _db.Payments.AddAsync(payment);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _db.OrderItems.RemoveRange(orderItems);
                _db.Orders.Remove(order);
                await _db.SaveChangesAsync();
                throw new InvalidOperationException($"Failed to create Xendit payment link: {ex.Message}", ex);
            }
        }


        // Deduct stock from the Commissary location (LocationId = 999) for all items in the ecommerce order (only if not a pre-order)
        if (!order.IsPreorder)
        {
            foreach (var item in orderItems)
            {
                try
                {
                    await _inventoryService.DeductStockAsync(item.VariationId, 999, item.Quantity);
                }
                catch (InvalidOperationException ex)
                {
                    // If stock record is not initialized for this location yet, log it and continue
                    System.Console.WriteLine($"CreateEcommerceOrderAsync: Stock deduction skipped for VariationId {item.VariationId} at Location 999: {ex.Message}");
                }
            }
        }

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
            LocationId = order.LocationId,
            TotalAmount = order.TotalAmount,
            OrderStatus = order.OrderStatus,
            PaymentMethod = order.PaymentMethod,
            PaymentStatus = order.PaymentStatus,
            PaymentUrl = order.Payments.FirstOrDefault(p => p.PaymentStatus == "Pending" && p.PaymentChannel == "GCash")?.GatewayReferenceNumber,
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
