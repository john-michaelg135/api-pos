using Api.Contracts.OrderManagement;

namespace Applications.Interfaces;

public interface IOrderManagementService
{
    // POS-013: Online order approval queue
    Task<List<OrderManagementResponseDto>> GetPendingApprovalAsync();
    Task<OrderManagementResponseDto?> ApproveOrderAsync(int orderId, ApproveOrderDto dto);
    Task<OrderManagementResponseDto?> RejectOrderAsync(int orderId, RejectOrderDto dto);

    // POS-014: Unified order list with filters
    Task<List<OrderManagementResponseDto>> GetAllOrdersAsync(OrderFilterDto filter);

    // POS-015: COD auto-mark paid on delivery confirm
    Task<OrderManagementResponseDto?> ConfirmDeliveryAsync(int orderId, int confirmedBy);

    // EC-012: Order progress tracking
    Task<OrderTrackingDto?> GetOrderTrackingAsync(int orderId);
}
