using Api.Contracts.Refund;

namespace Applications.Interfaces;

public interface IRefundNotificationService
{
    Task NotifyRefundApprovedAsync(int locationId, RefundResponseDto refund);
}
