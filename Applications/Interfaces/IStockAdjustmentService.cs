using Api.Contracts.StockAdjustment;

namespace Applications.Interfaces;

public interface IStockAdjustmentService
{
    // US-POS-019: Submit a damage/loss report — saved as "PendingApproval", stock NOT touched
    Task<StockAdjustmentResponseDto> SubmitAdjustmentAsync(CreateStockAdjustmentDto dto);

    // US-POS-020: Approve adjustment — executes the stock deduction and logs the manager
    Task<StockAdjustmentResponseDto?> ApproveAdjustmentAsync(int adjustmentId, ApproveStockAdjustmentDto dto);

    // Supporting: get all pending/processed adjustments for manager review
    Task<List<StockAdjustmentResponseDto>> GetAllAdjustmentsAsync();
}
