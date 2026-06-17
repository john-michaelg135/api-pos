using Microsoft.AspNetCore.SignalR;

namespace Api.Hubs;

public class RefundHub : Hub
{
    // Cashiers call this after connecting to receive alerts only for their location
    public async Task JoinLocationGroup(int locationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"location-{locationId}");
    }

    public async Task LeaveLocationGroup(int locationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"location-{locationId}");
    }
}
