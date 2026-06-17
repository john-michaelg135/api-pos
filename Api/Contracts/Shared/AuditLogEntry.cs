namespace Api.Contracts.Shared;

public class AuditLogEntry
{
    public string Source { get; set; } = "api-pos";
    public string Action { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public Guid? PerformedBy { get; set; }
    public DateTime Timestamp { get; set; }
    public object? Before { get; set; }
    public object? After { get; set; }
}
