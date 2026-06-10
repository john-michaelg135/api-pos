using Api.Contracts.Inventory;

namespace Applications.Interfaces;

public interface IInventoryService
{
    // POS-010: Stock receiving from SCMS
    Task<StockResponseDto> ReceiveStockAsync(StockReceivingDto dto);
    Task<List<StockReceivingResponseDto>> GetStockReceivingHistoryAsync();

    // POS-012: View current stock — per location or all
    Task<List<StockResponseDto>> GetStockByLocationAsync(int locationId);
    Task<List<StockResponseDto>> GetAllStockAsync();

    // POS-018: Low-stock alert query (qty < minThreshold, threshold > 0)
    Task<List<LowStockAlertDto>> GetLowStockAlertsAsync();

    // POS-011: Auto deduct stock on order confirm (called internally)
    Task DeductStockAsync(int variationId, int locationId, int quantity);

    // POS-017: Restore stock when a refund is approved (inverse of deduct)
    Task RestoreStockAsync(int variationId, int locationId, int quantity);

    // POS-019 & 020: Stock adjustments
    Task<StockAdjustmentResponseDto> SubmitStockAdjustmentAsync(StockAdjustmentRequestDto dto);
    Task<StockAdjustmentResponseDto> ApproveStockAdjustmentAsync(int adjustmentId, int approvedBy);
}
