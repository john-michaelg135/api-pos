namespace Api.Contracts.Analytics;

public class AnalyticsFilterDto
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int? LocationId { get; set; }
    public int? VariationId { get; set; }
    public string? OrderType { get; set; }   // Store, Bazaar, Online, Institutional
    public string? OrderSource { get; set; } // POS, Ecommerce
    public string? GroupBy { get; set; }     // Day, Week, Month
}
