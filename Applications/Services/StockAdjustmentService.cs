using Applications.Interfaces;
using Api.Contracts.StockAdjustment;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Api.Middlewares;

namespace Applications.Services;

public class StockAdjustmentService : IStockAdjustmentService
{
    private readonly PosDbContext _db;
    private readonly IInventoryService _inventoryService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuditLogService _auditLogService;

    public StockAdjustmentService(PosDbContext db, IInventoryService inventoryService, IHttpContextAccessor httpContextAccessor, IAuditLogService auditLogService)
    {
        _db = db;
        _inventoryService = inventoryService;
        _httpContextAccessor = httpContextAccessor;
        _auditLogService = auditLogService;
    }

    // ────────────────────────────────────────────────────
    // US-POS-019: Submit damage/loss report — saved as "PendingApproval"
    //             Stock is NOT modified at this stage
    // ────────────────────────────────────────────────────

    public async Task<StockAdjustmentResponseDto> SubmitAdjustmentAsync(CreateStockAdjustmentDto dto)
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

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier")
        {
            if (dto.LocationId != currentUser.LocationId)
                throw new InvalidOperationException("You are not authorized to submit stock adjustments for other locations.");
        }

        var validTypes = new[] { "Damage", "Loss", "Correction" };
        if (!validTypes.Contains(dto.AdjustmentType))
            throw new InvalidOperationException($"Invalid AdjustmentType. Must be one of: {string.Join(", ", validTypes)}.");

        if (dto.Quantity == 0)
            throw new InvalidOperationException("Quantity cannot be zero.");

        // US-POS-019: Status is hardcoded to "PendingApproval" — stock NOT touched yet
        var adjustment = new StockAdjustment
        {
            VariationId    = dto.VariationId,
            LocationId     = dto.LocationId,
            AdjustmentType = dto.AdjustmentType,
            Quantity       = dto.Quantity,
            Reason         = dto.Reason,
            Status         = "PendingApproval",
            SubmittedBy    = dto.SubmittedBy,
            CreatedAt      = DateTime.UtcNow
        };

        await _db.StockAdjustments.AddAsync(adjustment);
        await _db.SaveChangesAsync();

        var response = MapToResponse(adjustment, variation.VariationName, variation.Product?.ProductName ?? string.Empty, location.LocationName);

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

    // ────────────────────────────────────────────────────
    // US-POS-020: Approve adjustment — deduct stock and permanently log manager
    // ────────────────────────────────────────────────────

    public async Task<StockAdjustmentResponseDto?> ApproveAdjustmentAsync(int adjustmentId, ApproveStockAdjustmentDto dto)
    {
        var adjustment = await _db.StockAdjustments
            .Include(a => a.Variation)
                .ThenInclude(v => v!.Product)
            .Include(a => a.Location)
            .FirstOrDefaultAsync(a => a.AdjustmentId == adjustmentId);

        if (adjustment == null) return null;

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && adjustment.LocationId != currentUser.LocationId)
        {
            throw new InvalidOperationException("You are not authorized to approve stock adjustments for other locations.");
        }

        if (adjustment.Status != "PendingApproval")
            throw new InvalidOperationException($"Adjustment #{adjustmentId} is already '{adjustment.Status}'. Only PendingApproval adjustments can be approved.");

        // US-POS-020: Execute the actual stock deduction or addition
        if (adjustment.Quantity < 0)
        {
            await _inventoryService.DeductStockAsync(adjustment.VariationId, adjustment.LocationId, Math.Abs(adjustment.Quantity));
        }
        else
        {
            await _inventoryService.RestoreStockAsync(adjustment.VariationId, adjustment.LocationId, adjustment.Quantity);
        }

        // US-POS-020: Permanently log the approver's user_id
        var now = DateTime.UtcNow;
        adjustment.Status     = "Approved";
        adjustment.ApprovedBy = dto.ApprovedBy;
        adjustment.ApprovedAt = now;

        _db.StockAdjustments.Update(adjustment);
        await _db.SaveChangesAsync();

        var response = MapToResponse(
            adjustment,
            adjustment.Variation?.VariationName ?? string.Empty,
            adjustment.Variation?.Product?.ProductName ?? string.Empty,
            adjustment.Location?.LocationName ?? string.Empty);

        _auditLogService.Log(
            action: "Update",
            entity: "StockAdjustment",
            entityId: adjustment.AdjustmentId,
            before: new { Status = "PendingApproval" },
            after: new { Status = "Approved", ApprovedBy = dto.ApprovedBy },
            performedBy: null
        );

        return response;
    }

    // ────────────────────────────────────────────────────
    // Supporting: List all adjustments
    // ────────────────────────────────────────────────────

    public async Task<List<StockAdjustmentResponseDto>> GetAllAdjustmentsAsync()
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        var query = _db.StockAdjustments.AsQueryable();

        if (currentUser?.SubRole == "Cashier")
        {
            var locationId = currentUser.LocationId ?? 0;
            query = query.Where(a => a.LocationId == locationId);
        }

        var adjustments = await query
            .Include(a => a.Variation)
                .ThenInclude(v => v!.Product)
            .Include(a => a.Location)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

        return adjustments.Select(a => MapToResponse(
            a,
            a.Variation?.VariationName ?? string.Empty,
            a.Variation?.Product?.ProductName ?? string.Empty,
            a.Location?.LocationName ?? string.Empty)).ToList();
    }

    private static StockAdjustmentResponseDto MapToResponse(
        StockAdjustment a,
        string variationName,
        string productName,
        string locationName) => new()
    {
        AdjustmentId   = a.AdjustmentId,
        VariationId    = a.VariationId,
        VariationName  = variationName,
        ProductName    = productName,
        LocationId     = a.LocationId,
        LocationName   = locationName,
        AdjustmentType = a.AdjustmentType,
        Quantity       = a.Quantity,
        Reason         = a.Reason,
        Status         = a.Status,
        SubmittedBy    = a.SubmittedBy,
        ApprovedBy     = a.ApprovedBy,
        ApprovedAt     = a.ApprovedAt,
        CreatedAt      = a.CreatedAt
    };
}
