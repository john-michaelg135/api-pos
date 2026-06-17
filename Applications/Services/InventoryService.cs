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

        // Record the receiving event
        var receiving = new StockReceiving
        {
            VariationId = dto.VariationId,
            LocationId = dto.LocationId,
            QuantityReceived = dto.QuantityReceived,
            Notes = dto.Notes,
            ReceivedBy = dto.ReceivedBy,
            ReceivedAt = DateTime.UtcNow
        };

        await _db.StockReceivings.AddAsync(receiving);

        // Update or create the stock record
        var stock = await _db.Stocks
            .FirstOrDefaultAsync(s => s.VariationId == dto.VariationId && s.LocationId == dto.LocationId);

        var now = DateTime.UtcNow;

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

        await _db.SaveChangesAsync();


        if (dto.TransferId.HasValue)
        {
            try
            {
                await _scmsClient.UpdateTransferStatusAsync(dto.TransferId.Value, "Completed");
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"Error updating SCM transfer status for Transfer {dto.TransferId}: {ex.Message}");
            }
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

    public async Task DeductStockAsync(int variationId, int locationId, int quantity)
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
        await _db.SaveChangesAsync();
    }

    // ────────────────────────────────────────────────────
    // POS-017: Restore stock when a refund is approved
    // ────────────────────────────────────────────────────

    public async Task RestoreStockAsync(int variationId, int locationId, int quantity)
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
        }
        else
        {
            stock.Quantity  += quantity;
            stock.UpdatedAt  = now;
            _db.Stocks.Update(stock);
        }

        await _db.SaveChangesAsync();
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
