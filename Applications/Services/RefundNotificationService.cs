using Api.Contracts.Refund;
using Api.Hubs;
using Applications.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Applications.Services;

public class RefundNotificationService : IRefundNotificationService
{
    private readonly IHubContext<RefundHub> _hubContext;
    private readonly ILogger<RefundNotificationService> _logger;

    public RefundNotificationService(IHubContext<RefundHub> hubContext, ILogger<RefundNotificationService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyRefundApprovedAsync(int locationId, RefundResponseDto refund)
    {
        try
        {
            var groupName = $"location-{locationId}";
            await _hubContext.Clients.Group(groupName).SendAsync("RefundApproved", refund);
            _logger.LogInformation($"Sent RefundApproved notification to group {groupName} for RefundRequestId {refund.RefundRequestId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send RefundApproved notification for RefundRequestId {refund.RefundRequestId}");
        }
    }
}
