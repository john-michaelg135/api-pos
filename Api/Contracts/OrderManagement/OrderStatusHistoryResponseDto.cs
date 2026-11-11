using System;

namespace Api.Contracts.OrderManagement;

public class OrderStatusHistoryResponseDto
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string OldStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public int? ChangedBy { get; set; }
    // Display name for ChangedBy, when it's a recorded staff user.
    public string? ChangedByName { get; set; }
    public string? Remarks { get; set; }
    public DateTime CreatedAt { get; set; }
}
