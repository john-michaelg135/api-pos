using Applications.Interfaces;
using Api.Contracts.Inventory;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class InventoryService : IInventoryService
{
    private readonly PosDbContext _db;

    public InventoryService(PosDbContext db)
    {
        _db = db;
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

        return new StockResponseDto
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
    }

    // ────────────────────────────────────────────────────
    // POS-012: View current stock per location
    // ────────────────────────────────────────────────────

    public async Task<List<StockResponseDto>> GetStockByLocationAsync(int locationId)
    {
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
        var stocks = await _db.Stocks
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
        var lowStocks = await _db.Stocks
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
}
