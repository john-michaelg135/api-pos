using Api.Contracts.Inventory;

namespace Applications.Interfaces;

public interface IInventoryService
{
    // POS-010: Stock receiving from SCMS
    Task<StockResponseDto> ReceiveStockAsync(StockReceivingDto dto);

    // POS-012: View current stock — per location or all
    Task<List<StockResponseDto>> GetStockByLocationAsync(int locationId);
    Task<List<StockResponseDto>> GetAllStockAsync();

    // POS-018: Low-stock alert query (qty < minThreshold, threshold > 0)
    Task<List<LowStockAlertDto>> GetLowStockAlertsAsync();

    // POS-011: Auto deduct stock on order confirm (called internally)
    Task DeductStockAsync(int variationId, int locationId, int quantity);
}
