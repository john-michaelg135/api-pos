using Applications.Interfaces;
using Api.Contracts.Inventory;
using Api.Contracts.Scms;
using Infrastructures.Externals;

namespace Applications.Services;

public class ScmsIntegrationService : IScmsIntegrationService
{
    private readonly ScmsApiClient _scmsClient;
    private readonly IInventoryService _inventoryService;

    public ScmsIntegrationService(ScmsApiClient scmsClient, IInventoryService inventoryService)
    {
        _scmsClient = scmsClient;
        _inventoryService = inventoryService;
    }

    // ────────────────────────────────────────────────────
    // US-POS-028: Pull finished goods from SCMS and update local stock
    // ────────────────────────────────────────────────────

    public async Task<ScmsPullSummaryDto> PullAndReceiveDeliveriesAsync()
    {
        var payload = await _scmsClient.PullBulkDeliveriesAsync();

        var summary = new ScmsPullSummaryDto
        {
            TotalDeliveriesReceived = payload.Deliveries.Count,
            PulledAt = DateTime.UtcNow
        };

        foreach (var delivery in payload.Deliveries)
        {
            try
            {
                // US-POS-028: Update local stock totals based on SCMS payload
                await _inventoryService.ReceiveStockAsync(new StockReceivingDto
                {
                    VariationId      = delivery.VariationId,
                    LocationId       = delivery.LocationId,
                    QuantityReceived = delivery.QuantityDelivered,
                    Notes            = $"[SCMS Pull] Ref: {delivery.DeliveryReference} | {delivery.Notes}".Trim(' ', '|'),
                    ReceivedBy       = null  // System-initiated — no specific user
                });

                summary.TotalItemsUpdated++;
            }
            catch (InvalidOperationException ex)
            {
                summary.Errors.Add($"VariationId={delivery.VariationId}, LocationId={delivery.LocationId}: {ex.Message}");
            }
        }

        summary.TotalErrors = summary.Errors.Count;
        return summary;
    }

    public async Task<bool> ConfirmTransferAsync(string transferId, ReceiveConfirmationDto dto)
    {
        return await _scmsClient.SendReceiveConfirmationAsync(transferId, dto);
    }
}
