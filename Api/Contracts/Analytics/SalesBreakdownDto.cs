namespace Api.Contracts.Analytics;

public class SalesBreakdownDto
{
    public string Label { get; set; } = string.Empty;  // Location name, variation name, or order type/source
    public decimal TotalRevenue { get; set; }
    public int TotalOrders { get; set; }
    public int TotalUnitsSold { get; set; }
}
