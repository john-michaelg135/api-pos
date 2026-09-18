using Api.Contracts.Scms;
using Applications.Interfaces;
using Domains.Entities;
using Infrastructures.Externals;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

/// <summary>
/// Compares SCM stock-transfer manifests against what POS actually received
/// (StockReceiving rows + resolved transfer items) and reports/heals drift.
/// </summary>
public class ReconciliationService : IReconciliationService
{
    private readonly PosDbContext _db;
    private readonly ScmsApiClient _scmsClient;

    public ReconciliationService(PosDbContext db, ScmsApiClient scmsClient)
    {
        _db = db;
        _scmsClient = scmsClient;
    }

    public async Task<ReconciliationReportDto> BuildReportAsync(bool onlyDiscrepancies = false)
    {
        var transfers = await _db.StockTransfers
            .Include(t => t.Items)
            .OrderByDescending(t => t.TransferDate)
            .ToListAsync();

        var report = new ReconciliationReportDto
        {
            GeneratedAt = DateTime.UtcNow,
            TotalTransfers = transfers.Count
        };

        foreach (var transfer in transfers)
        {
            var detail = BuildTransferDetail(transfer);

            // Roll-up counters (counted regardless of the detail filter).
            if (detail.ReconciliationState == "Matched") report.MatchedTransfers++;
            if (detail.ReconciliationState == "Discrepancy") report.DiscrepancyTransfers++;
            if (detail.ReceivedAt == null) report.UnreceivedTransfers++;
            if (detail.ReceivedAt != null && detail.StatusSyncState != "Acknowledged")
                report.PendingStatusCallbacks++;

            if (!onlyDiscrepancies || detail.Issues.Count > 0)
                report.Transfers.Add(detail);
        }

        return report;
    }

    public async Task<ReconciliationResyncResultDto> ResyncTransferAsync(string transferId)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.TransferId == transferId);

        var result = new ReconciliationResyncResultDto { TransferId = transferId };

        if (transfer == null)
        {
            result.Messages.Add($"Transfer '{transferId}' not found.");
            result.ReconciliationState = "Unreconciled";
            return result;
        }

        // 1. Re-match every line to a POS variation by SKU (then ScmsProductId fallback).
        var rematched = await RematchLinesAsync(transfer);
        result.LinesRematched = rematched;

        // 2. Recompute the reconciliation verdict from current receipts.
        var detail = BuildTransferDetail(transfer);
        transfer.ReconciliationState = detail.ReconciliationState;
        transfer.ReconciledAt = DateTime.UtcNow;
        result.ReconciliationState = detail.ReconciliationState;
        foreach (var issue in detail.Issues) result.Messages.Add(issue);

        // 3. Re-attempt the SCM status callback when the transfer was received but
        //    SCM never acknowledged it.
        if (transfer.ReceivedAt != null && transfer.StatusSyncState != "Acknowledged")
        {
            try
            {
                await _scmsClient.UpdateTransferStatusAsync(transfer.TransferId, "Completed");
                transfer.StatusSyncState = "Acknowledged";
                transfer.StatusSyncedAt = DateTime.UtcNow;
                result.StatusCallbackResent = true;
                result.StatusCallbackAcknowledged = true;
                result.Messages.Add("SCM status callback re-sent and acknowledged.");
            }
            catch (Exception ex)
            {
                transfer.StatusSyncState = "Failed";
                result.StatusCallbackResent = true;
                result.StatusCallbackAcknowledged = false;
                result.Messages.Add($"SCM status callback failed: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync();
        return result;
    }

    public async Task<int> RetryFailedStatusCallbacksAsync(CancellationToken cancellationToken = default)
    {
        // Transfers received in POS but not yet acknowledged by SCM (Pending or Failed).
        var pending = await _db.StockTransfers
            .Where(t => t.ReceivedAt != null && t.StatusSyncState != "Acknowledged")
            .ToListAsync(cancellationToken);

        var healed = 0;

        foreach (var transfer in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await _scmsClient.UpdateTransferStatusAsync(transfer.TransferId, "Completed");
                transfer.StatusSyncState = "Acknowledged";
                transfer.StatusSyncedAt = DateTime.UtcNow;
                healed++;
            }
            catch (Exception)
            {
                // Leave it Failed; the next sweep will retry.
                transfer.StatusSyncState = "Failed";
            }
        }

        if (healed > 0 || pending.Count > 0)
            await _db.SaveChangesAsync(cancellationToken);

        return healed;
    }

    // ── helpers ──

    /// <summary>
    /// Resolve each transfer line to a POS variation, preferring the canonical SKU,
    /// then the SCM product id stored on Product.ScmsProductId. Returns how many
    /// lines had their ResolvedVariationId set/changed.
    /// </summary>
    private async Task<int> RematchLinesAsync(StockTransfer transfer)
    {
        var changed = 0;

        foreach (var item in transfer.Items)
        {
            int? resolved = null;

            if (!string.IsNullOrWhiteSpace(item.Sku))
            {
                resolved = await _db.ProductVariations
                    .Where(v => v.Sku != null && v.Sku.ToLower() == item.Sku!.ToLower())
                    .Select(v => (int?)v.VariationId)
                    .FirstOrDefaultAsync();
            }

            if (resolved == null && !string.IsNullOrWhiteSpace(item.ProductId))
            {
                // Fallback: match SCM product id against Product.ScmsProductId,
                // then take that product's single active variation if unambiguous.
                var variationIds = await _db.ProductVariations
                    .Where(v => v.IsActive
                        && v.Product.ScmsProductId != null
                        && v.Product.ScmsProductId == item.ProductId)
                    .Select(v => v.VariationId)
                    .ToListAsync();

                if (variationIds.Count == 1) resolved = variationIds[0];
            }

            if (resolved != item.ResolvedVariationId)
            {
                item.ResolvedVariationId = resolved;
                changed++;
            }
        }

        return changed;
    }

    /// <summary>Compute the reconciliation detail for one loaded transfer (no DB writes).</summary>
    private TransferReconciliationDto BuildTransferDetail(StockTransfer transfer)
    {
        var detail = new TransferReconciliationDto
        {
            TransferId = transfer.TransferId,
            DestinationBranchId = transfer.DestinationBranchId,
            DestinationBranchName = transfer.DestinationBranchName,
            Status = transfer.Status,
            StatusSyncState = transfer.StatusSyncState,
            ReceivedAt = transfer.ReceivedAt,
            ReconciledAt = transfer.ReconciledAt
        };

        var hasDiscrepancy = false;

        foreach (var item in transfer.Items)
        {
            var line = new LineReconciliationDto
            {
                ProductId = item.ProductId,
                Sku = item.Sku,
                ProductName = item.ProductName,
                ResolvedVariationId = item.ResolvedVariationId,
                ExpectedQuantity = item.Quantity,
                ReceivedQuantity = item.ReceivedQuantity,
                DamagedQuantity = item.DamagedQuantity,
                Variance = (item.ReceivedQuantity ?? 0) - item.Quantity
            };

            if (item.ResolvedVariationId == null)
            {
                line.LineState = "Unmatched";
                detail.Issues.Add($"Line '{item.ProductName}' (SKU {item.Sku ?? item.ProductId}) has no matching POS variation.");
                hasDiscrepancy = true;
            }
            else if (item.ReceivedQuantity == null)
            {
                line.LineState = "NotReceived";
            }
            else if (item.ReceivedQuantity < item.Quantity)
            {
                line.LineState = "Short";
                detail.Issues.Add($"Line '{item.ProductName}' short by {item.Quantity - item.ReceivedQuantity} unit(s).");
                hasDiscrepancy = true;
            }
            else if (item.ReceivedQuantity > item.Quantity)
            {
                line.LineState = "Over";
                detail.Issues.Add($"Line '{item.ProductName}' over by {item.ReceivedQuantity - item.Quantity} unit(s).");
                hasDiscrepancy = true;
            }
            else
            {
                line.LineState = "Matched";
            }

            if (item.DamagedQuantity > 0)
                detail.Issues.Add($"Line '{item.ProductName}' reported {item.DamagedQuantity} damaged unit(s).");

            detail.TotalExpected += item.Quantity;
            detail.TotalReceived += item.ReceivedQuantity ?? 0;
            detail.TotalDamaged += item.DamagedQuantity;
            detail.Lines.Add(line);
        }

        // A transfer that has been received but never acknowledged by SCM is a drift
        // worth surfacing, even if quantities balanced.
        if (transfer.ReceivedAt != null && transfer.StatusSyncState != "Acknowledged")
            detail.Issues.Add("Received in POS but SCM status callback not acknowledged.");

        detail.ReconciliationState = hasDiscrepancy ? "Discrepancy"
            : transfer.ReceivedAt == null ? "Unreconciled"
            : "Matched";

        return detail;
    }
}
