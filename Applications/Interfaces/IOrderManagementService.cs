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

    // Institutional: Shipped → Completed with proof of delivery
    Task<OrderManagementResponseDto?> ConfirmInstitutionalDeliveryAsync(int orderId, ConfirmInstitutionalDeliveryDto dto);
    Task<string?> GetDeliveryProofImageAsync(int orderId);

    // Generic order status update (Order Manager only)
    Task<OrderManagementResponseDto?> UpdateOrderStatusAsync(int orderId, UpdateOrderStatusDto dto);
    Task<OrderManagementResponseDto?> CompletePaymentAsync(int orderId, int confirmedBy);

    // EC-012: Order progress tracking
    Task<OrderTrackingDto?> GetOrderTrackingAsync(int orderId);

    // POS-016 & 017: Refunds
    Task<OrderManagementResponseDto?> RequestRefundAsync(int orderId, string reason, string? proofImage);
    Task<string?> GetRefundProofImageAsync(int orderId);
    Task<OrderManagementResponseDto?> ApproveRefundAsync(int orderId, int approvedBy, string? pinCode);
    Task<OrderManagementResponseDto?> RejectRefundAsync(int orderId, int rejectedBy, string reason, string? pinCode);

    // POS-xxx: Void workflow
    Task<OrderManagementResponseDto?> RequestVoidAsync(int orderId, string reason);
    Task<OrderManagementResponseDto?> ApproveVoidAsync(int orderId, int approvedBy, string? pinCode);
    Task<OrderManagementResponseDto?> RejectVoidAsync(int orderId, int rejectedBy, string reason, string pinCode);
}
