using Api.Contracts.Scms;
using Api.Middlewares;
using Domains.Entities;
using Infrastructures.Externals;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

/// <summary>
/// Receive Stock for SCMS deliveries: lists the deliveries waiting at the caller's branches and
/// books a branch's count into stock. Lines are matched to POS variations by SKU; only the good
/// units go into stock, and SCMS is told the expected, received and damaged counts.
/// </summary>
public class TransferReceivingService
{
    private readonly PosDbContext _db;
    private readonly ScmsApiClient _scms;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<TransferReceivingService> _logger;

    public TransferReceivingService(PosDbContext db, ScmsApiClient scms, IHttpContextAccessor httpContextAccessor, ILogger<TransferReceivingService> logger)
    {
        _db = db;
        _scms = scms;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<List<PendingTransferDto>> GetPendingAsync()
    {
        var query = _db.StockTransfers.AsNoTracking().Include(t => t.Items).Where(t => t.Status != "Completed");

        // Branch staff see deliveries for their own branches only; super users see all.
        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (user is { IsSuperUser: false })
        {
            var allowed = user.LocationIds.Select(id => id.ToString()).ToArray();
            query = query.Where(t => allowed.Contains(t.DestinationBranchId));
        }

        var transfers = await query.OrderBy(t => t.TransferDate).ToListAsync();
        var bySku = await LoadVariationsBySkuAsync();
        var locationNames = await _db.Locations.AsNoTracking().ToDictionaryAsync(l => l.LocationId, l => l.LocationName);

        return transfers.Select(t =>
        {
            int.TryParse(t.DestinationBranchId, out var locationId);
            return new PendingTransferDto
            {
                TransferId = t.TransferId,
                SourceLocationName = t.SourceLocationName,
                DestinationLocationId = locationId,
                DestinationLocationName = locationNames.GetValueOrDefault(locationId, t.DestinationBranchName),
                TransferDate = t.TransferDate,
                Status = t.Status,
                Lines = t.Items.OrderBy(i => i.Id).Select(i =>
                {
                    var match = Match(i, bySku);
                    return new PendingTransferLineDto
                    {
                        LineId = i.Id,
                        Sku = i.Sku,
                        ProductName = i.ProductName,
                        ExpectedQuantity = i.Quantity,
                        VariationId = match?.VariationId,
                        VariationName = match?.VariationName,
                        PosProductName = match?.Product?.ProductName,
                    };
                }).ToList(),
            };
        }).ToList();
    }

    /// <summary>
    /// Books a delivery into the branch's stock. Throws KeyNotFoundException when the delivery
    /// doesn't exist (or isn't the caller's), InvalidOperationException for a bad count.
    /// </summary>
    public async Task<string> ReceiveAsync(string transferId, ReceiveTransferRequest request)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.TransferId == transferId)
            ?? throw new KeyNotFoundException("Delivery not found.");

        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (!int.TryParse(transfer.DestinationBranchId, out var locationId))
            throw new InvalidOperationException("This delivery's branch is not a POS branch.");
        if (user is { IsSuperUser: false } && !user.LocationIds.Contains(locationId))
            throw new KeyNotFoundException("Delivery not found.");
        if (transfer.Status == "Completed")
            throw new InvalidOperationException("This delivery was already received.");

        var counts = request.Lines.ToDictionary(l => l.LineId);
        var bySku = await LoadVariationsBySkuAsync();
        var notes = request.Notes?.Trim() ?? string.Empty;
        if (notes.Length > 500) throw new InvalidOperationException("Notes must be 500 characters or fewer.");

        // Check every line before touching stock, so a bad count changes nothing.
        var plan = new List<(StockTransferItem Item, ProductVariation Variation, ReceiveTransferLineRequest Count)>();
        foreach (var item in transfer.Items.OrderBy(i => i.Id))
        {
            var label = string.IsNullOrWhiteSpace(item.Sku) ? item.ProductName : item.Sku;
            if (!counts.TryGetValue(item.Id, out var count))
                throw new InvalidOperationException($"Enter the count for {label}.");
            var variation = Match(item, bySku)
                ?? throw new InvalidOperationException($"{label} is not in the POS catalog yet, so it can't be received.");
            if (count.ReceivedQuantity < 0 || count.DamagedQuantity < 0)
                throw new InvalidOperationException($"Counts for {label} can't be negative.");
            if (count.ReceivedQuantity + count.DamagedQuantity > item.Quantity)
                throw new InvalidOperationException($"{label}: received plus damaged is more than the {item.Quantity} sent.");
            var reason = count.DiscrepancyReason?.Trim();
            if (reason is { Length: > 300 })
                throw new InvalidOperationException($"{label}: the reason must be 300 characters or fewer.");
            if (count.ReceivedQuantity != item.Quantity && string.IsNullOrEmpty(reason))
                throw new InvalidOperationException($"{label}: give a reason for the {item.Quantity - count.ReceivedQuantity} units not received.");
            count.DiscrepancyReason = string.IsNullOrEmpty(reason) ? null : reason;
            plan.Add((item, variation, count));
        }

        var now = DateTime.UtcNow;
        foreach (var (item, variation, count) in plan)
        {
            item.ResolvedVariationId = variation.VariationId;
            item.ReceivedQuantity = count.ReceivedQuantity;
            item.DamagedQuantity = count.DamagedQuantity;
            if (count.ReceivedQuantity == 0) continue;

            var lineNote = $"SCMS delivery {transfer.TransferId}";
            if (count.DamagedQuantity > 0) lineNote += $" · {count.DamagedQuantity} damaged";
            if (count.DiscrepancyReason is not null) lineNote += $" · {count.DiscrepancyReason}";

            _db.StockReceivings.Add(new StockReceiving
            {
                VariationId = variation.VariationId,
                LocationId = locationId,
                QuantityReceived = count.ReceivedQuantity,
                Notes = lineNote,
                ReceivedAt = now,
                TransferId = transfer.TransferId,
            });

            var stock = await _db.Stocks.FirstOrDefaultAsync(s => s.VariationId == variation.VariationId && s.LocationId == locationId);
            if (stock is null)
            {
                _db.Stocks.Add(new Stock { VariationId = variation.VariationId, LocationId = locationId, Quantity = count.ReceivedQuantity, UpdatedAt = now });
            }
            else
            {
                stock.Quantity += count.ReceivedQuantity;
                stock.UpdatedAt = now;
            }
        }

        transfer.Status = "Completed";
        transfer.ReceivedAt = now;
        transfer.ReconciliationState = plan.All(p => p.Count.ReceivedQuantity == p.Item.Quantity) ? "Matched" : "Discrepancy";
        transfer.ReconciledAt = now;
        await _db.SaveChangesAsync();

        // Stock is saved; telling SCMS is best effort. A failure is marked so the
        // reconciliation sweep re-sends the status later.
        var confirmation = new ReceiveConfirmationDto
        {
            TransferId = transfer.TransferId,
            BranchId = transfer.DestinationBranchId,
            ReceivedAt = now,
            ReceivedBy = user?.PortalUserId ?? user?.Username ?? string.Empty,
            ReceivedByName = user?.DisplayName ?? user?.Username ?? string.Empty,
            Status = "Completed",
            Notes = notes,
            ReceivedProducts = plan.Select(p => new ReceivedProductDto
            {
                ProductId = p.Item.ProductId,
                Sku = p.Item.Sku,
                ExpectedQuantity = p.Item.Quantity,
                ReceivedQuantity = p.Count.ReceivedQuantity,
                DamagedQuantity = p.Count.DamagedQuantity,
                DiscrepancyReason = p.Count.DiscrepancyReason,
            }).ToList(),
        };

        try
        {
            await _scms.SendReceiveConfirmationAsync(transfer.TransferId, confirmation);
            transfer.StatusSyncState = "Acknowledged";
            transfer.StatusSyncedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            transfer.StatusSyncState = "Failed";
            _logger.LogWarning("Receive confirmation for {TransferId} didn't reach SCMS: {Reason}", transfer.TransferId, ex.Message);
        }
        await _db.SaveChangesAsync();

        var total = plan.Sum(p => p.Count.ReceivedQuantity);
        return transfer.StatusSyncState == "Acknowledged"
            ? $"Received {total} units into stock. SCMS was notified."
            : $"Received {total} units into stock. SCMS couldn't be reached; it will be notified automatically.";
    }

    // Active variations keyed by SKU: the SKU column, or, for variations made before the
    // integration, the first segment of the name ("SKU|Pouch|250g").
    private async Task<Dictionary<string, ProductVariation>> LoadVariationsBySkuAsync()
    {
        var variations = await _db.ProductVariations.AsNoTracking()
            .Include(v => v.Product)
            .Where(v => v.IsActive)
            .ToListAsync();

        var bySku = new Dictionary<string, ProductVariation>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variations)
        {
            var sku = !string.IsNullOrWhiteSpace(v.Sku) ? v.Sku.Trim() : v.VariationName.Split('|')[0].Trim();
            if (sku.Length > 0) bySku.TryAdd(sku, v);
        }
        return bySku;
    }

    private static ProductVariation? Match(StockTransferItem item, Dictionary<string, ProductVariation> bySku) =>
        !string.IsNullOrWhiteSpace(item.Sku) && bySku.TryGetValue(item.Sku.Trim(), out var v) ? v : null;
}
