namespace Api.Contracts.Analytics;

public class RevenueSummaryDto
{
    public string Period { get; set; } = string.Empty;   // e.g. "2026-05-15", "2026-W20", "2026-05"
    public decimal TotalRevenue { get; set; }
    public int TotalOrders { get; set; }
    public int TotalUnitsSold { get; set; }
}
