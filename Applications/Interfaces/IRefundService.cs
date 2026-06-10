using Api.Contracts.Refund;

namespace Applications.Interfaces;

public interface IRefundService
{
    // US-POS-016: Submit a refund request — always saved as "Pending"
    Task<RefundResponseDto> SubmitRefundAsync(CreateRefundRequestDto dto);

    // US-POS-017: Approve a refund — restores stock and logs the manager
    Task<RefundResponseDto?> ApproveRefundAsync(int refundRequestId, ApproveRefundDto dto);

    // Supporting: get all refund requests for manager review
    Task<List<RefundResponseDto>> GetAllRefundsAsync();
}
