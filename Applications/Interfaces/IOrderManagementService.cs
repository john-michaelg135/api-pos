using Api.Contracts.OrderManagement;

namespace Applications.Interfaces;

/// <summary>
/// A proof photo ready for the viewer: a fresh five-minute signed HTTPS address for a stored File,
/// or the saved data URL for a photo from before the move to file storage.
/// </summary>
public sealed record ProofImageResult(string ProofImage, string Source, bool Pending)
{
    public const string FileSource = "file";
    public const string LegacySource = "legacy";
}

/// <summary>The order has a proof File, but storage can't hand out a view address right now (503) or lost it (404).</summary>
public sealed class ProofImageUnavailableException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

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
    Task<ProofImageResult?> GetDeliveryProofImageAsync(int orderId);

    // Generic order status update (Order Manager only)
    Task<OrderManagementResponseDto?> UpdateOrderStatusAsync(int orderId, UpdateOrderStatusDto dto);
    Task<OrderManagementResponseDto?> CompletePaymentAsync(int orderId, int confirmedBy);

    // EC-012: Order progress tracking
    Task<OrderTrackingDto?> GetOrderTrackingAsync(int orderId);

    // POS-016 & 017: Refunds
    Task<OrderManagementResponseDto?> RequestRefundAsync(int orderId, string reason, string? proofImage, IReadOnlyList<RefundItemDto>? items = null, string? proofReference = null);
    Task<ProofImageResult?> GetRefundProofImageAsync(int orderId);
    Task<OrderManagementResponseDto?> ApproveRefundAsync(int orderId, int approvedBy, string? pinCode);
    Task<OrderManagementResponseDto?> RejectRefundAsync(int orderId, int rejectedBy, string reason, string? pinCode);

    // POS-xxx: Void workflow
    Task<OrderManagementResponseDto?> RequestVoidAsync(int orderId, string reason);
    Task<OrderManagementResponseDto?> ApproveVoidAsync(int orderId, int approvedBy, string? pinCode);
    Task<OrderManagementResponseDto?> RejectVoidAsync(int orderId, int rejectedBy, string reason, string pinCode);
}
