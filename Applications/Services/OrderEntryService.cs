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

    public async Task<List<ProductGridItemDto>> GetProductGridAsync(int? locationId = null, bool includeUnpriced = false)
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier")
        {
            locationId = currentUser.LocationId;
        }

        var query = _db.ProductVariations
            .AsNoTracking()
            .Where(v => v.IsActive && v.Product.IsActive);

        // For a branch, only what that branch carries: variations with a stock record there
        // (received or adjusted at some point), not the whole catalog.
        if (locationId.HasValue)
            query = query.Where(v => _db.Stocks.Any(s => s.VariationId == v.VariationId && s.LocationId == locationId.Value));

        // Unpriced variations (e.g. just synced from SCMS) can't be sold until a manager prices
        // them; the sales screen asks for them anyway so it can show them as "No price set".
        if (!includeUnpriced)
            query = query.Where(v => v.ProductPrices.Any(pp => pp.IsActive && pp.Price > 0));

        var gridItems = await query
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
                    .Where(pp => pp.IsActive && pp.Price > 0)
                    .Select(pp => pp.Price)
                    .FirstOrDefault(),
                HasPrice        = v.ProductPrices.Any(pp => pp.IsActive && pp.Price > 0)
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
        if (currentUser != null && !PosRoleAuthorization.IsAdministrator(currentUser))
        {
            if (!dto.LocationId.HasValue || !currentUser.LocationIds.Contains(dto.LocationId.Value))
                throw new InvalidOperationException("You are not authorized to create orders for other locations.");
        }

        await EnsureShiftIsOpenAsync(dto.LocationId, now);

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

        // In-store GCash is paid to the company wallet and checked by the cashier before
        // confirming, like cash, so the sale completes immediately; no Xendit invoice is created.
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
            PaymentStatus = "Paid",
            OrderStatus = "Completed",
            TotalAmount = 0,              // Will be calculated below
            AmountTendered = isGcash ? null : dto.AmountTendered,
            ChangeAmount = isGcash ? null : dto.ChangeAmount,
            CreatedAt = now,
            UpdatedAt = now,
            ContactPerson = dto.ContactPerson,
            DeliveryAddress = dto.DeliveryAddress,
            InstitutionalStreet = dto.InstitutionalStreet,
            InstitutionalCity = dto.InstitutionalCity,
            InstitutionalProvince = dto.InstitutionalProvince,
            InstitutionalZipCode = dto.InstitutionalZipCode,
            CustomVariationNotes = dto.CustomVariationNotes,
            SeniorPwdId = dto.SeniorPwdId,
            SeniorPwdName = dto.SeniorPwdName,
            SeniorPwdStreet = dto.SeniorPwdStreet,
            SeniorPwdBarangay = dto.SeniorPwdBarangay,
            SeniorPwdCity = dto.SeniorPwdCity,
            SeniorPwdProvince = dto.SeniorPwdProvince,
            SeniorPwdZipCode = dto.SeniorPwdZipCode
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
                .Where(pp => pp.VariationId == cartItem.VariationId && pp.IsActive && pp.Price > 0)
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

        await _db.Payments.AddAsync(new Payment
        {
            OrderId = order.OrderId,
            AmountPaid = order.TotalAmount,
            PaymentChannel = dto.PaymentMethod,
            PaymentStatus = "Success",
            PaidAt = now
        });
        await _db.SaveChangesAsync();

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

        if (currentUser != null && !PosRoleAuthorization.IsAdministrator(currentUser))
        {
            var allowedLocations = currentUser.LocationIds;
            query = query.Where(order =>
                order.LocationId.HasValue && allowedLocations.Contains(order.LocationId.Value));
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
        if (currentUser != null && !PosRoleAuthorization.IsAdministrator(currentUser) &&
            (!orderDto.LocationId.HasValue || !currentUser.LocationIds.Contains(orderDto.LocationId.Value)))
        {
            throw new InvalidOperationException("You are not authorized to view orders from other locations.");
        }

        return orderDto;
    }

    private void EnsureCurrentUserCanAccessOrderLocation(int? locationId)
    {
        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (user != null && !PosRoleAuthorization.IsAdministrator(user) &&
            (!locationId.HasValue || !user.LocationIds.Contains(locationId.Value)))
        {
            throw new InvalidOperationException("You are not authorized to access orders outside your assigned location.");
        }
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
        EnsureCurrentUserCanAccessOrderLocation(order.LocationId);

        if (order.OrderStatus == "Completed")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already completed.");

        var oldStatus = order.OrderStatus;
        var now = DateTime.UtcNow;
        order.OrderStatus = "Completed";
        order.PaymentStatus = "Paid";
        order.ApprovedBy = dto.SubmittedBy;
        order.ApprovedAt = now;
        order.UpdatedAt = now;

        _db.Orders.Update(order);

        // Record status history
        var history = new OrderStatusHistory
        {
            OrderId = order.OrderId,
            OldStatus = oldStatus,
            NewStatus = "Completed",
            ChangedBy = dto.SubmittedBy,
            Remarks = "Walk-in order confirmed",
            CreatedAt = now
        };
        await _db.OrderStatusHistories.AddAsync(history);

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

    private async Task EnsureShiftIsOpenAsync(int? locationId, DateTime now)
    {
        if (!locationId.HasValue)
            return;

        var businessDate = DateOnly.FromDateTime(now.AddHours(8));
        var shiftIsClosed = await _db.StartingCashEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.LocationId == locationId.Value
                && entry.BusinessDate == businessDate
                && entry.EndShiftAt.HasValue);

        if (shiftIsClosed)
            throw new InvalidOperationException("This shift has ended. Sales cannot be processed until the next business day.");
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

        await EnsureShiftIsOpenAsync(dto.LocationId, now);

        // Validate location if provided
        if (dto.LocationId.HasValue)
        {
            var locationExists = await _db.Locations.AnyAsync(l => l.LocationId == dto.LocationId.Value);
            if (!locationExists)
                throw new InvalidOperationException($"Location with ID {dto.LocationId.Value} not found.");
        }

        if (string.IsNullOrWhiteSpace(dto.CustomVariationNotes))
            throw new InvalidOperationException("Notes / customization is required for institutional orders.");

        // Validate all variation IDs
        foreach (var cartItem in dto.Items)
        {
            var variationExists = await _db.ProductVariations.AnyAsync(v => v.VariationId == cartItem.VariationId);
            if (!variationExists)
                throw new InvalidOperationException($"Product variation with ID {cartItem.VariationId} not found.");
        }

        var isCash = string.Equals(dto.PaymentMethod, "Cash", StringComparison.OrdinalIgnoreCase);
        var isGcash = string.Equals(dto.PaymentMethod, "GCash", StringComparison.OrdinalIgnoreCase);

        if (dto.IsPartialPayment && !isGcash)
            throw new InvalidOperationException("Partial payment is only available for GCash.");

        // Price the cart before creating the order so a failed payment check leaves no orphan order.
        decimal totalAmount = 0;
        var pricedItems = new List<(CartItemDto Item, decimal UnitPrice, decimal Subtotal)>();
        foreach (var cartItem in dto.Items)
        {
            var activePrice = await _db.ProductPrices
                .Where(pp => pp.VariationId == cartItem.VariationId && pp.IsActive && pp.Price > 0)
                .FirstOrDefaultAsync();

            if (activePrice == null)
                throw new InvalidOperationException($"No active price found for variation ID {cartItem.VariationId}");

            var unitPrice = activePrice.Price;
            var subtotal = unitPrice * cartItem.Quantity;
            totalAmount += subtotal;
            pricedItems.Add((cartItem, unitPrice, subtotal));
        }

        if (isCash && (dto.AmountTendered ?? 0) < totalAmount)
            throw new InvalidOperationException("Amount paid is not enough. Cash payments must cover the full order total.");

        // Cash and full GCash are settled up front and go straight to Paid (ready for shipment).
        // A GCash downpayment keeps the order in Processing until the balance is confirmed.
        // COD is collected later.
        var isPartial = isGcash && dto.IsPartialPayment;
        var amountPaidNow = isCash || isGcash
            ? (isPartial ? Math.Round(totalAmount * 0.5m, 2, MidpointRounding.AwayFromZero) : totalAmount)
            : 0m;

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
            PaymentMethod = isGcash ? "GCash" : dto.PaymentMethod,
            PaymentStatus = isPartial ? "Partial" : (isCash || isGcash ? "Paid" : "Pending"),
            OrderStatus = isPartial || !(isCash || isGcash) ? "Processing" : "Paid",
            TotalAmount = totalAmount,
            AmountTendered = isCash ? dto.AmountTendered : null,
            ChangeAmount = isCash ? dto.AmountTendered - totalAmount : null,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _db.Orders.AddAsync(order);
        await _db.SaveChangesAsync();

        await _db.OrderItems.AddRangeAsync(pricedItems.Select(p => new OrderItem
        {
            OrderId = order.OrderId,
            VariationId = p.Item.VariationId,
            Quantity = p.Item.Quantity,
            UnitPrice = p.UnitPrice,
            Subtotal = p.Subtotal,
            CreatedAt = now
        }));

        if (amountPaidNow > 0)
        {
            await _db.Payments.AddAsync(new Payment
            {
                OrderId = order.OrderId,
                PaymentChannel = order.PaymentMethod,
                AmountPaid = amountPaidNow,
                PaymentStatus = "Success",
                PaidAt = now
            });
        }

        await _db.OrderStatusHistories.AddAsync(new OrderStatusHistory
        {
            OrderId = order.OrderId,
            OldStatus = "New",
            NewStatus = order.OrderStatus,
            ChangedBy = dto.SubmittedBy,
            Remarks = isPartial
                ? $"50% GCash downpayment of ₱{amountPaidNow:N2} confirmed in wallet. Balance of ₱{totalAmount - amountPaidNow:N2} outstanding."
                : isGcash
                    ? $"Full GCash payment of ₱{amountPaidNow:N2} confirmed in wallet."
                    : isCash ? $"Paid in full with cash (₱{amountPaidNow:N2})." : null,
            CreatedAt = now
        });

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
                .Where(pp => pp.VariationId == cartItem.VariationId && pp.IsActive && pp.Price > 0)
                .FirstOrDefaultAsync();

            if (activePrice == null)
                throw new InvalidOperationException($"No active price found for variation ID {cartItem.VariationId}");

            var basePrice = cartItem.Price ?? activePrice.Price;
            var finalPrice = dto.ApplyPwdDiscount
                ? Math.Round((basePrice / 1.12m) * 0.80m, 2)
                : basePrice;

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
            AmountTendered = order.AmountTendered,
            ChangeAmount = order.ChangeAmount,
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
