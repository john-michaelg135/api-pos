using Applications.Audit;
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
    private readonly IPosStaffIdentity _staff;

    public StockAdjustmentService(PosDbContext db, IInventoryService inventoryService, IHttpContextAccessor httpContextAccessor, IAuditLogService auditLogService, IPosStaffIdentity staff)
    {
        _db = db;
        _inventoryService = inventoryService;
        _httpContextAccessor = httpContextAccessor;
        _auditLogService = auditLogService;
        _staff = staff;
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

        await _auditLogService.RecordAsync(StockAudit.AdjustmentSubmitted(adjustment, StockAudit.ItemLabel(variation), location.LocationName));

        return MapToResponse(adjustment, variation.VariationName, variation.Product?.ProductName ?? string.Empty, location.LocationName);
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
            await _inventoryService.DeductStockAsync(
                adjustment.VariationId,
                adjustment.LocationId,
                Math.Abs(adjustment.Quantity),
                movementType: "Adjustment",
                reference: $"Adjustment #{adjustment.AdjustmentId}");
        }
        else
        {
            await _inventoryService.RestoreStockAsync(
                adjustment.VariationId,
                adjustment.LocationId,
                adjustment.Quantity,
                movementType: "Adjustment",
                reference: $"Adjustment #{adjustment.AdjustmentId}");
        }

        // US-POS-020: Permanently log the approver's user_id
        var now = DateTime.UtcNow;
        adjustment.Status     = "Approved";
        adjustment.ApprovedBy = dto.ApprovedBy;
        adjustment.ApprovedAt = now;

        _db.StockAdjustments.Update(adjustment);
        await _db.SaveChangesAsync();

        await _auditLogService.RecordAsync(StockAudit.AdjustmentApproved(
            adjustment, StockAudit.ItemLabel(adjustment.Variation), adjustment.Location?.LocationName, "PendingApproval"));

        return MapToResponse(
            adjustment,
            adjustment.Variation?.VariationName ?? string.Empty,
            adjustment.Variation?.Product?.ProductName ?? string.Empty,
            adjustment.Location?.LocationName ?? string.Empty);
    }

    // ────────────────────────────────────────────────────
    // Supporting: List all adjustments
    // ────────────────────────────────────────────────────

    public async Task<List<StockAdjustmentResponseDto>> GetAllAdjustmentsAsync()
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        // System losses are listed on their own (GetStockLossesAsync).
        var query = _db.StockAdjustments.Where(a => a.AdjustmentType != InventoryService.StockLossType);

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

    // ────────────────────────────────────────────────────
    // System loss: a manager writes off units that can't be sold (expired, damaged,
    // mishandled). The manager is the approver, so stock goes down straight away.
    // ────────────────────────────────────────────────────

    public async Task<StockLossResponseDto> RecordStockLossAsync(RecordStockLossDto dto)
    {
        if (dto.Quantity <= 0 || dto.Quantity > 10_000)
            throw new InvalidOperationException("Quantity lost must be between 1 and 10,000.");

        var reason = StockLossReasons.All.FirstOrDefault(r => string.Equals(r, dto.LossReason?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Reason must be one of: {string.Join(", ", StockLossReasons.All)}.");
        var notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
        if (notes is { Length: > 500 })
            throw new InvalidOperationException("Notes must not exceed 500 characters.");
        if (reason == StockLossReasons.Other && notes is null)
            throw new InvalidOperationException("Describe what happened when the reason is Other.");

        var variation = await _db.ProductVariations
            .Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.VariationId == dto.VariationId)
            ?? throw new InvalidOperationException($"Product variation with ID {dto.VariationId} not found.");
        var location = await _db.Locations.FindAsync(dto.LocationId)
            ?? throw new InvalidOperationException($"Location with ID {dto.LocationId} not found.");

        var onHand = await _db.Stocks
            .AsNoTracking()
            .Where(s => s.VariationId == dto.VariationId && s.LocationId == dto.LocationId)
            .Select(s => (int?)s.Quantity)
            .FirstOrDefaultAsync() ?? 0;
        if (onHand < dto.Quantity)
            throw new InvalidOperationException($"Only {onHand} unit(s) are in stock at {location.LocationName}; you can't write off {dto.Quantity}.");

        // The selling price at the time, so the report can say how much the loss was worth.
        var unitPrice = await _db.ProductPrices
            .AsNoTracking()
            .Where(p => p.VariationId == dto.VariationId && p.IsActive && p.Price > 0)
            .Select(p => (decimal?)p.Price)
            .FirstOrDefaultAsync();

        var staffId = await _staff.CurrentStaffIdAsync();
        var now = DateTime.UtcNow;
        var loss = new StockAdjustment
        {
            VariationId = dto.VariationId,
            LocationId = dto.LocationId,
            AdjustmentType = InventoryService.StockLossType,
            Quantity = -dto.Quantity,
            Reason = notes is null ? reason : $"{reason}: {notes}",
            LossReason = reason,
            UnitPrice = unitPrice,
            Status = "Approved",
            SubmittedBy = staffId ?? 0,
            ApprovedBy = staffId,
            ApprovedAt = now,
            CreatedAt = now,
        };

        // The loss record and the stock deduction land together or not at all.
        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        await _db.StockAdjustments.AddAsync(loss);
        await _db.SaveChangesAsync();
        await _inventoryService.DeductStockAsync(
            dto.VariationId,
            dto.LocationId,
            dto.Quantity,
            orderId: null,
            movementType: InventoryService.StockLossType,
            reference: $"Stock loss #{loss.AdjustmentId}",
            notes: loss.Reason);
        if (transaction is not null) await transaction.CommitAsync();

        await _auditLogService.RecordAsync(StockAudit.LossRecorded(loss, StockAudit.ItemLabel(variation), location.LocationName));

        var names = staffId.HasValue ? await _staff.NamesAsync([staffId.Value]) : new Dictionary<int, string>();
        return MapLoss(loss, variation, location.LocationName, names);
    }

    public async Task<List<StockLossResponseDto>> GetStockLossesAsync(int? locationId, DateTime? from, DateTime? to)
    {
        var query = _db.StockAdjustments
            .AsNoTracking()
            .Include(a => a.Variation)
                .ThenInclude(v => v!.Product)
            .Include(a => a.Location)
            .Where(a => a.AdjustmentType == InventoryService.StockLossType);

        if (locationId.HasValue) query = query.Where(a => a.LocationId == locationId.Value);
        if (from.HasValue) query = query.Where(a => a.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(a => a.CreatedAt < to.Value);

        var losses = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(1000)
            .ToListAsync();

        var staffIds = losses.Select(a => a.ApprovedBy ?? a.SubmittedBy).Where(id => id > 0).Distinct().ToArray();
        var names = staffIds.Length == 0 ? new Dictionary<int, string>() : await _staff.NamesAsync(staffIds);

        return losses
            .Select(a => MapLoss(a, a.Variation, a.Location?.LocationName ?? string.Empty, names))
            .ToList();
    }

    private static StockLossResponseDto MapLoss(
        StockAdjustment a,
        ProductVariation? variation,
        string locationName,
        IReadOnlyDictionary<int, string> names)
    {
        var quantity = Math.Abs(a.Quantity);
        var reason = a.LossReason ?? StockLossReasons.Other;
        var prefix = $"{reason}: ";
        return new StockLossResponseDto
        {
            LossId = a.AdjustmentId,
            VariationId = a.VariationId,
            VariationName = variation?.VariationName ?? string.Empty,
            ProductName = variation?.Product?.ProductName ?? string.Empty,
            LocationId = a.LocationId,
            LocationName = locationName,
            Quantity = quantity,
            LossReason = reason,
            Notes = a.Reason.StartsWith(prefix, StringComparison.Ordinal) ? a.Reason[prefix.Length..] : null,
            UnitPrice = a.UnitPrice,
            LossValue = a.UnitPrice * quantity,
            RecordedByName = names.GetValueOrDefault(a.ApprovedBy ?? a.SubmittedBy),
            RecordedAt = a.CreatedAt,
        };
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
