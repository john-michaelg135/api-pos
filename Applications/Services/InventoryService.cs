using Applications.Interfaces;
using Api.Contracts.Inventory;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Api.Middlewares;
using Infrastructures.Externals;

namespace Applications.Services;

public class InventoryService : IInventoryService
{
    private readonly PosDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
private readonly ScmsApiClient _scmsClient;
private readonly IAuditLogService _auditLogService;

    public InventoryService(PosDbContext db, IHttpContextAccessor httpContextAccessor, ScmsApiClient scmsClient, IAuditLogService auditLogService)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _scmsClient = scmsClient;
        _auditLogService = auditLogService;
    }


    // ────────────────────────────────────────────────────
    // POS-010: Stock receiving from SCMS
    // ────────────────────────────────────────────────────

    public async Task<StockResponseDto> ReceiveStockAsync(StockReceivingDto dto)
    {
        // Validate variation exists
        var variation = await _db.ProductVariations
            .Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.VariationId == dto.VariationId);

        if (variation == null)
            throw new InvalidOperationException($"Product variation with ID {dto.VariationId} not found.");

        // Validate location exists
        var location = await _db.Locations.FindAsync(dto.LocationId);
        if (location == null)
            throw new InvalidOperationException($"Location with ID {dto.LocationId} not found.");

        // Record the receiving event (link to the SCM transfer for reconciliation)
        var receiving = new StockReceiving
        {
            VariationId = dto.VariationId,
            LocationId = dto.LocationId,
            QuantityReceived = dto.QuantityReceived,
            Notes = dto.Notes,
            ReceivedBy = dto.ReceivedBy,
            ReceivedAt = DateTime.UtcNow,
            TransferId = dto.TransferId?.ToString()
        };

        await _db.StockReceivings.AddAsync(receiving);

        // Update or create the stock record
        var stock = await _db.Stocks
            .FirstOrDefaultAsync(s => s.VariationId == dto.VariationId && s.LocationId == dto.LocationId);

        var now = DateTime.UtcNow;

        var quantityBefore = stock?.Quantity ?? 0;
        if (stock == null)
        {
            // First time receiving this variation at this location
            stock = new Stock
            {
                VariationId = dto.VariationId,
                LocationId = dto.LocationId,
                Quantity = dto.QuantityReceived,
                UpdatedAt = now
            };
            await _db.Stocks.AddAsync(stock);
        }
        else
        {
            // Add to existing stock
            stock.Quantity += dto.QuantityReceived;
            stock.UpdatedAt = now;
            _db.Stocks.Update(stock);
        }

        await AddMovementAsync(
            dto.VariationId,
            dto.LocationId,
            dto.QuantityReceived,
            "Receipt",
            dto.TransferId.HasValue && dto.TransferId.Value > 0 ? $"Transfer #{dto.TransferId.Value}" : "Manual receipt",
            null,
            dto.Notes);
        await _db.SaveChangesAsync();


        // Only SCM-linked receipts (a real, positive TransferId) touch the SCM
        // reconciliation + status callback. Manual entries send TransferId 0 and
        // must NOT reach out to the SCM service (which may be unset/unreachable
        // in some environments) — otherwise a manual receive fails there.
        if (dto.TransferId.HasValue && dto.TransferId.Value > 0)
        {
            // Reconciliation bookkeeping: stamp receipt on the linked transfer + its matched line.
            var transferId = dto.TransferId.Value.ToString();
            var transfer = await _db.StockTransfers
                .Include(t => t.Items)
                .FirstOrDefaultAsync(t => t.TransferId == transferId);

            if (transfer != null)
            {
                transfer.ReceivedAt = now;

                // Record the received quantity against the matched line (by resolved variation,
                // falling back to the first not-yet-received line for this variation).
                var line = transfer.Items.FirstOrDefault(i => i.ResolvedVariationId == dto.VariationId)
                    ?? transfer.Items.FirstOrDefault(i => i.ReceivedQuantity == null);
                if (line != null)
                {
                    line.ResolvedVariationId = dto.VariationId;
                    line.ReceivedQuantity = (line.ReceivedQuantity ?? 0) + dto.QuantityReceived;
                }
            }

            try
            {
                await _scmsClient.UpdateTransferStatusAsync(transferId, "Completed");
                if (transfer != null)
                {
                    transfer.StatusSyncState = "Acknowledged";
                    transfer.StatusSyncedAt = DateTime.UtcNow;
                }
            }
            catch (System.Exception ex)
            {
                // Best-effort: POS stock is already updated. Mark the callback failed so
                // reconciliation can detect and re-attempt it later.
                if (transfer != null) transfer.StatusSyncState = "Failed";
                System.Console.WriteLine($"Error updating SCM transfer status for Transfer {dto.TransferId}: {ex.Message}");
            }

            if (transfer != null) await _db.SaveChangesAsync();
        }

        var response = new StockResponseDto
        {
            StockId = stock.StockId,
            VariationId = stock.VariationId,
            VariationName = variation.VariationName,
            ProductName = variation.Product?.ProductName ?? string.Empty,
            LocationId = stock.LocationId,
            LocationName = location.LocationName,
            Quantity = stock.Quantity,
            UpdatedAt = stock.UpdatedAt
        };

        _auditLogService.Log(
            action: "Create",
            entity: "StockReceiving",
            entityId: receiving.ReceivingId,
            before: null,
            after: response,
            performedBy: null
        );

        return response;
    }

    public async Task<List<StockReceivingResponseDto>> GetStockReceivingHistoryAsync()
    {
        var history = await _db.StockReceivings
            .Include(sr => sr.Variation)
                .ThenInclude(v => v!.Product)
            .Include(sr => sr.Location)
            .OrderByDescending(sr => sr.ReceivedAt)
            .ToListAsync();

        return history.Select(sr => new StockReceivingResponseDto
        {
            ReceivingId = sr.ReceivingId,
            VariationId = sr.VariationId,
            VariationName = sr.Variation?.VariationName ?? string.Empty,
            ProductName = sr.Variation?.Product?.ProductName ?? string.Empty,
            LocationId = sr.LocationId,
            LocationName = sr.Location?.LocationName ?? string.Empty,
            QuantityReceived = sr.QuantityReceived,
            Notes = sr.Notes,
            ReceivedBy = sr.ReceivedBy,
            ReceivedAt = sr.ReceivedAt
        }).ToList();
    }

    // ────────────────────────────────────────────────────
    // POS-012: View current stock per location
    // ────────────────────────────────────────────────────

    public async Task<List<StockResponseDto>> GetStockByLocationAsync(int locationId)
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId != locationId)
        {
            throw new InvalidOperationException("You are not authorized to view stocks for other locations.");
        }

        var stocks = await _db.Stocks
            .Include(s => s.Variation)
                .ThenInclude(v => v!.Product)
            .Include(s => s.Location)
            .Where(s => s.LocationId == locationId)
            .OrderBy(s => s.Variation!.Product!.ProductName)
            .ToListAsync();

        return stocks.Select(s => new StockResponseDto
        {
            StockId = s.StockId,
            VariationId = s.VariationId,
            VariationName = s.Variation?.VariationName ?? string.Empty,
            ProductName = s.Variation?.Product?.ProductName ?? string.Empty,
            LocationId = s.LocationId,
            LocationName = s.Location?.LocationName ?? string.Empty,
            Quantity = s.Quantity,
            UpdatedAt = s.UpdatedAt
        }).ToList();
    }

    // ────────────────────────────────────────────────────
    // POS-012 (extended): View all stock across all locations
    // ────────────────────────────────────────────────────

    public async Task<List<StockResponseDto>> GetAllStockAsync()
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        var query = _db.Stocks.AsQueryable();

        if (currentUser?.SubRole == "Cashier")
        {
            var locationId = currentUser.LocationId ?? 0;
            query = query.Where(s => s.LocationId == locationId);
        }

        var stocks = await query
            .Include(s => s.Variation)
                .ThenInclude(v => v!.Product)
            .Include(s => s.Location)
            .OrderBy(s => s.Variation!.Product!.ProductName)
            .ThenBy(s => s.Variation!.VariationName)
            .ToListAsync();

        return stocks.Select(s => new StockResponseDto
        {
            StockId = s.StockId,
            VariationId = s.VariationId,
            VariationName = s.Variation?.VariationName ?? string.Empty,
            ProductName = s.Variation?.Product?.ProductName ?? string.Empty,
            LocationId = s.LocationId,
            LocationName = s.Location?.LocationName ?? string.Empty,
            Quantity = s.Quantity,
            UpdatedAt = s.UpdatedAt
        }).ToList();
    }

    // ────────────────────────────────────────────────────
    // POS-018: Low-stock threshold alerts
    // ────────────────────────────────────────────────────

    public async Task<List<LowStockAlertDto>> GetLowStockAlertsAsync()
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        var query = _db.Stocks.AsQueryable();

        if (currentUser?.SubRole == "Cashier")
        {
            var locationId = currentUser.LocationId ?? 0;
            query = query.Where(s => s.LocationId == locationId);
        }

        var lowStocks = await query
            .Include(s => s.Variation)
                .ThenInclude(v => v!.Product)
            .Include(s => s.Location)
            .Where(s => s.MinThreshold > 0 && s.Quantity < s.MinThreshold)
            .OrderBy(s => s.Variation!.Product!.ProductName)
            .ToListAsync();

        return lowStocks.Select(s => new LowStockAlertDto
        {
            StockId = s.StockId,
            VariationId = s.VariationId,
            VariationName = s.Variation?.VariationName ?? string.Empty,
            ProductName = s.Variation?.Product?.ProductName ?? string.Empty,
            LocationId = s.LocationId,
            LocationName = s.Location?.LocationName ?? string.Empty,
            Quantity = s.Quantity,
            MinThreshold = s.MinThreshold
        }).ToList();
    }

    // ────────────────────────────────────────────────────
    // POS-011: Auto deduct stock when order is confirmed
    // ────────────────────────────────────────────────────

    public async Task DeductStockAsync(
        int variationId,
        int locationId,
        int quantity,
        int? orderId = null,
        string movementType = "Sale",
        string? reference = null)
    {
        var stock = await _db.Stocks
            .FirstOrDefaultAsync(s => s.VariationId == variationId && s.LocationId == locationId);

        if (stock == null)
            throw new InvalidOperationException($"No stock record found for variation {variationId} at location {locationId}.");

        if (stock.Quantity < quantity)
            throw new InvalidOperationException($"Insufficient stock. Available: {stock.Quantity}, Requested: {quantity}.");

        stock.Quantity -= quantity;
        stock.UpdatedAt = DateTime.UtcNow;

        _db.Stocks.Update(stock);
        await AddMovementAsync(
            variationId,
            locationId,
            -quantity,
            movementType,
            reference ?? (orderId.HasValue ? $"Order #{orderId.Value}" : null),
            orderId);
        await _db.SaveChangesAsync();
    }

    // ────────────────────────────────────────────────────
    // POS-017: Restore stock when a refund is approved
    // ────────────────────────────────────────────────────

    public async Task RestoreStockAsync(
        int variationId,
        int locationId,
        int quantity,
        int? orderId = null,
        string movementType = "Refund",
        string? reference = null)
    {
        var stock = await _db.Stocks
            .FirstOrDefaultAsync(s => s.VariationId == variationId && s.LocationId == locationId);

        var now = DateTime.UtcNow;

        if (stock == null)
        {
            // No stock record yet — create one with the returned quantity
            stock = new Stock
            {
                VariationId = variationId,
                LocationId  = locationId,
                Quantity    = quantity,
                UpdatedAt   = now
            };
            await _db.Stocks.AddAsync(stock);
            await AddMovementAsync(
                variationId,
                locationId,
                quantity,
                movementType,
                reference ?? (orderId.HasValue ? $"Order #{orderId.Value}" : null),
                orderId);
        }
        else
        {
            stock.Quantity  += quantity;
            stock.UpdatedAt  = now;
            _db.Stocks.Update(stock);
            await AddMovementAsync(
                variationId,
                locationId,
                quantity,
                movementType,
                reference ?? (orderId.HasValue ? $"Order #{orderId.Value}" : null),
                orderId);
        }

        await _db.SaveChangesAsync();
    }

    public async Task RecordStockLossAsync(
        int variationId,
        int locationId,
        int quantity,
        int? orderId = null,
        string? reference = null,
        string? notes = null)
    {
        if (quantity <= 0)
            throw new InvalidOperationException("Stock loss quantity must be greater than zero.");

        // The sale deduction already removed these units from stock. Keep the loss
        // as a separate negative ledger movement without changing the stock row.
        await AddMovementAsync(
            variationId,
            locationId,
            -quantity,
            "Loss",
            reference ?? (orderId.HasValue ? $"Order #{orderId.Value}" : null),
            orderId,
            notes ?? $"System loss: {quantity} unsellable unit(s).");
        await _db.SaveChangesAsync();
    }

    public async Task<List<StockMovementResponseDto>> GetStockMovementsAsync(
        int? locationId,
        DateTime? from,
        DateTime? to)
    {
        var query = _db.StockMovements
            .AsNoTracking()
            .Include(m => m.Variation)
                .ThenInclude(v => v!.Product)
            .Include(m => m.Location)
            .AsQueryable();

        if (locationId.HasValue) query = query.Where(m => m.LocationId == locationId.Value);
        if (from.HasValue) query = query.Where(m => m.OccurredAt >= from.Value);
        if (to.HasValue) query = query.Where(m => m.OccurredAt < to.Value);

        var movements = await query
            .OrderByDescending(m => m.OccurredAt)
            .ThenByDescending(m => m.StockMovementId)
            .Take(1000)
            .ToListAsync();

        var existingKeys = movements
            .Where(m => m.OrderId.HasValue)
            .Select(m => (m.OrderId!.Value, m.VariationId, m.QuantityDelta))
            .ToHashSet();

        var legacyOrders = await _db.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
                .ThenInclude(item => item.ProductVariation)
            .Include(o => o.Location)
            .Where(o =>
                o.LocationId.HasValue
                && (!locationId.HasValue || o.LocationId == locationId.Value)
                && o.PaymentStatus == "Paid"
                && o.OrderStatus != "Cancelled"
                && o.OrderStatus != "Voided"
                && (!from.HasValue || o.CreatedAt >= from.Value)
                && (!to.HasValue || o.CreatedAt < to.Value))
            .OrderByDescending(o => o.CreatedAt)
            .Take(500)
            .ToListAsync();

        foreach (var order in legacyOrders)
        {
            foreach (var item in order.OrderItems)
            {
                var key = (order.OrderId, item.VariationId, -item.Quantity);
                if (!existingKeys.Contains(key))
                {
                    movements.Add(new StockMovement
                    {
                        StockMovementId = -order.OrderId,
                        VariationId = item.VariationId,
                        LocationId = order.LocationId!.Value,
                        QuantityDelta = -item.Quantity,
                        MovementType = "Sale",
                        Reference = order.OrderNumber,
                        OrderId = order.OrderId,
                        OccurredAt = order.CreatedAt,
                        Variation = item.ProductVariation,
                        Location = order.Location,
                    });
                }
            }
        }

        var legacyReceivings = await _db.StockReceivings
            .AsNoTracking()
            .Include(receiving => receiving.Variation)
                .ThenInclude(variation => variation!.Product)
            .Include(receiving => receiving.Location)
            .Where(receiving =>
                (!locationId.HasValue || receiving.LocationId == locationId.Value)
                && (!from.HasValue || receiving.ReceivedAt >= from.Value)
                && (!to.HasValue || receiving.ReceivedAt < to.Value))
            .ToListAsync();

        movements.AddRange(legacyReceivings
            .Where(receiving => !movements.Any(m =>
                m.MovementType == "Receipt"
                && m.LocationId == receiving.LocationId
                && m.VariationId == receiving.VariationId
                && m.QuantityDelta == receiving.QuantityReceived
                && Math.Abs((m.OccurredAt - receiving.ReceivedAt).TotalSeconds) <= 5))
            .Select(receiving => new StockMovement
            {
                StockMovementId = -1_000_000_000L - receiving.ReceivingId,
                VariationId = receiving.VariationId,
                LocationId = receiving.LocationId,
                QuantityDelta = receiving.QuantityReceived,
                MovementType = "Receipt",
                Reference = receiving.TransferId is { Length: > 0 } ? $"Transfer #{receiving.TransferId}" : "Manual receipt",
                Notes = receiving.Notes,
                OccurredAt = receiving.ReceivedAt,
                Variation = receiving.Variation,
                Location = receiving.Location,
            }));

        var legacyAdjustments = await _db.StockAdjustments
            .AsNoTracking()
            .Include(adjustment => adjustment.Variation)
                .ThenInclude(variation => variation!.Product)
            .Include(adjustment => adjustment.Location)
            .Where(adjustment =>
                adjustment.Status == "Approved"
                && adjustment.ApprovedAt.HasValue
                && (!locationId.HasValue || adjustment.LocationId == locationId.Value)
                && (!from.HasValue || adjustment.ApprovedAt >= from.Value)
                && (!to.HasValue || adjustment.ApprovedAt < to.Value))
            .ToListAsync();

        movements.AddRange(legacyAdjustments
            .Where(adjustment => !movements.Any(m =>
                m.MovementType == "Adjustment"
                && m.LocationId == adjustment.LocationId
                && m.VariationId == adjustment.VariationId
                && m.QuantityDelta == adjustment.Quantity
                && Math.Abs((m.OccurredAt - adjustment.ApprovedAt!.Value).TotalSeconds) <= 5))
            .Select(adjustment => new StockMovement
            {
                StockMovementId = -2_000_000_000L - adjustment.AdjustmentId,
                VariationId = adjustment.VariationId,
                LocationId = adjustment.LocationId,
                QuantityDelta = adjustment.Quantity,
                MovementType = "Adjustment",
                Reference = $"Adjustment #{adjustment.AdjustmentId}",
                Notes = adjustment.Reason,
                OccurredAt = adjustment.ApprovedAt!.Value,
                Variation = adjustment.Variation,
                Location = adjustment.Location,
            }));

        var legacyRefunds = await _db.RefundRequests
            .AsNoTracking()
            .Include(refund => refund.Variation)
                .ThenInclude(variation => variation!.Product)
            .Where(refund =>
                refund.Status == "Approved"
                && refund.ApprovedAt.HasValue
                && (!locationId.HasValue || refund.LocationId == locationId.Value)
                && (!from.HasValue || refund.ApprovedAt >= from.Value)
                && (!to.HasValue || refund.ApprovedAt < to.Value))
            .ToListAsync();

        movements.AddRange(legacyRefunds
            .Where(refund => !movements.Any(m =>
                m.MovementType == "Refund"
                && m.OrderId == refund.OrderId
                && m.VariationId == refund.VariationId
                && m.QuantityDelta == refund.QuantityToReturn
                && Math.Abs((m.OccurredAt - refund.ApprovedAt!.Value).TotalSeconds) <= 5))
            .Select(refund => new StockMovement
            {
                StockMovementId = -3_000_000_000L - refund.RefundRequestId,
                VariationId = refund.VariationId,
                LocationId = refund.LocationId,
                QuantityDelta = refund.QuantityToReturn,
                MovementType = "Refund",
                Reference = $"Refund #{refund.RefundRequestId}",
                OrderId = refund.OrderId,
                OccurredAt = refund.ApprovedAt!.Value,
                Variation = refund.Variation,
                Location = null,
            }));

        var variationIds = movements.Select(m => m.VariationId).Distinct().ToArray();
        var currentStocks = await _db.Stocks
            .AsNoTracking()
            .Where(s => (!locationId.HasValue || s.LocationId == locationId.Value)
                && (variationIds.Length == 0 || variationIds.Contains(s.VariationId)))
            .ToDictionaryAsync(s => (s.LocationId, s.VariationId), s => s.Quantity);
        var locationNames = await _db.Locations
            .AsNoTracking()
            .Where(location => !locationId.HasValue || location.LocationId == locationId.Value)
            .ToDictionaryAsync(location => location.LocationId, location => location.LocationName);

        // Older system-loss rows were initially recorded with a zero delta. Recover
        // their displayed quantity from the originating order item.
        var legacyLosses = movements
            .Where(m => m.MovementType == "Loss" && m.QuantityDelta == 0 && m.OrderId.HasValue)
            .Select(m => m.OrderId!.Value)
            .Distinct()
            .ToArray();
        if (legacyLosses.Length > 0)
        {
            var lossQuantities = await _db.OrderItems
                .AsNoTracking()
                .Where(item => legacyLosses.Contains(item.OrderId))
                .GroupBy(item => new { item.OrderId, item.VariationId })
                .Select(group => new { group.Key.OrderId, group.Key.VariationId, Quantity = group.Sum(item => item.Quantity) })
                .ToListAsync();

            foreach (var movement in movements.Where(m => m.MovementType == "Loss" && m.QuantityDelta == 0 && m.OrderId.HasValue))
            {
                var quantity = lossQuantities
                    .FirstOrDefault(item => item.OrderId == movement.OrderId!.Value && item.VariationId == movement.VariationId)
                    ?.Quantity;
                if (quantity.HasValue)
                    movement.QuantityDelta = -quantity.Value;
            }
        }

        return movements
            .GroupBy(m => (m.LocationId, m.VariationId))
            .SelectMany(group =>
            {
                var ordered = group
                    .OrderBy(m => m.OccurredAt)
                    .ThenBy(m => m.StockMovementId)
                    .ToList();
                var quantityAfterAll = currentStocks.GetValueOrDefault(group.Key);
                    var quantityBeforeAll = quantityAfterAll - ordered.Sum(m => m.QuantityDelta);
                    var running = quantityBeforeAll;
                    return ordered.Select(m =>
                    {
                        var before = running;
                        running += m.QuantityDelta;
                    return new StockMovementResponseDto
                    {
                        StockMovementId = m.StockMovementId,
                        VariationId = m.VariationId,
                        VariationName = m.Variation?.VariationName ?? string.Empty,
                        ProductName = m.Variation?.Product?.ProductName ?? string.Empty,
                        LocationId = m.LocationId,
                        LocationName = m.Location?.LocationName
                            ?? locationNames.GetValueOrDefault(m.LocationId, string.Empty),
                        QuantityDelta = m.QuantityDelta,
                        MovementType = m.MovementType,
                        Reference = m.Reference,
                        Notes = m.Notes,
                        OrderId = m.OrderId,
                        OccurredAt = m.OccurredAt,
                        QuantityBefore = before,
                        QuantityAfter = running,
                    };
                });
            })
            .OrderByDescending(m => m.OccurredAt)
            .ThenByDescending(m => m.StockMovementId)
            .ToList();
    }

    private async Task AddMovementAsync(
        int variationId,
        int locationId,
        int quantityDelta,
        string movementType,
        string? reference,
        int? orderId,
        string? notes = null)
    {
        await _db.StockMovements.AddAsync(new StockMovement
        {
            VariationId = variationId,
            LocationId = locationId,
            QuantityDelta = quantityDelta,
            MovementType = movementType,
            Reference = reference,
            OrderId = orderId,
            Notes = notes,
            OccurredAt = DateTime.UtcNow,
        });
    }

    // ────────────────────────────────────────────────────
    // POS-019 & 020: Stock adjustments
    // ────────────────────────────────────────────────────

    public async Task<StockAdjustmentResponseDto> SubmitStockAdjustmentAsync(StockAdjustmentRequestDto dto)
    {
        var adjustment = new StockAdjustment
        {
            VariationId = dto.VariationId,
            LocationId = dto.LocationId,
            Quantity = dto.Quantity,
            AdjustmentType = dto.AdjustmentType,
            Reason = dto.Reason,
            Status = "PendingApproval",
            SubmittedBy = dto.SubmittedBy,
            CreatedAt = DateTime.UtcNow
        };

        await _db.StockAdjustments.AddAsync(adjustment);
        await _db.SaveChangesAsync();

        var response = new StockAdjustmentResponseDto
        {
            AdjustmentId = adjustment.AdjustmentId,
            VariationId = adjustment.VariationId,
            LocationId = adjustment.LocationId,
            Quantity = adjustment.Quantity,
            AdjustmentType = adjustment.AdjustmentType,
            Reason = adjustment.Reason,
            Status = adjustment.Status,
            SubmittedBy = adjustment.SubmittedBy,
            CreatedAt = adjustment.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };

        _auditLogService.Log(
            action: "Create",
            entity: "StockAdjustment",
            entityId: adjustment.AdjustmentId,
            before: null,
            after: response,
            performedBy: null
        );

        return response;
    }

    public async Task<StockAdjustmentResponseDto> ApproveStockAdjustmentAsync(int adjustmentId, int approvedBy)
    {
        var adjustment = await _db.StockAdjustments.FindAsync(adjustmentId);
        if (adjustment == null)
            throw new InvalidOperationException($"Adjustment {adjustmentId} not found.");

        if (adjustment.Status != "Pending" && adjustment.Status != "PendingApproval")
            throw new InvalidOperationException($"Adjustment is already {adjustment.Status}.");

        // Apply the quantity change to stock
        var stock = await _db.Stocks
            .FirstOrDefaultAsync(s => s.VariationId == adjustment.VariationId && s.LocationId == adjustment.LocationId);

        if (stock == null)
        {
            // If it's a positive adjustment, create the stock
            if (adjustment.Quantity > 0)
            {
                stock = new Stock
                {
                    VariationId = adjustment.VariationId,
                    LocationId = adjustment.LocationId,
                    Quantity = adjustment.Quantity,
                    UpdatedAt = DateTime.UtcNow
                };
                await _db.Stocks.AddAsync(stock);
            }
            else
            {
                throw new InvalidOperationException($"No stock record found for variation {adjustment.VariationId} at location {adjustment.LocationId}. Cannot deduct.");
            }
        }
        else
        {
            if (adjustment.Quantity < 0 && stock.Quantity + adjustment.Quantity < 0)
            {
                throw new InvalidOperationException($"Insufficient stock for deduction. Available: {stock.Quantity}, Requested deduction: {Math.Abs(adjustment.Quantity)}.");
            }
            
            stock.Quantity += adjustment.Quantity;
            stock.UpdatedAt = DateTime.UtcNow;
            _db.Stocks.Update(stock);
        }

        adjustment.Status = "Approved";
        adjustment.ApprovedBy = approvedBy;
        adjustment.ApprovedAt = DateTime.UtcNow;
        _db.StockAdjustments.Update(adjustment);

        await _db.SaveChangesAsync();

        var response = new StockAdjustmentResponseDto
        {
            AdjustmentId = adjustment.AdjustmentId,
            VariationId = adjustment.VariationId,
            LocationId = adjustment.LocationId,
            Quantity = adjustment.Quantity,
            AdjustmentType = adjustment.AdjustmentType,
            Reason = adjustment.Reason,
            Status = adjustment.Status,
            SubmittedBy = adjustment.SubmittedBy,
            ApprovedBy = adjustment.ApprovedBy,
            CreatedAt = adjustment.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };

        _auditLogService.Log(
            action: "Update",
            entity: "StockAdjustment",
            entityId: adjustment.AdjustmentId,
            before: new { Status = "Pending" },
            after: new { Status = "Approved", ApprovedBy = approvedBy },
            performedBy: null
        );

        return response;
    }
}
