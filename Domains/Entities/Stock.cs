namespace Domains.Entities;

public class Stock
{
    public int StockId { get; set; }
    public int VariationId { get; set; }        // FK to ProductVariation
    public int LocationId { get; set; }         // FK to Location
    public int Quantity { get; set; } = 0;
    public int MinThreshold { get; set; } = 0;  // POS-018: low-stock alert if Quantity < MinThreshold (0 = disabled)
    public DateTime UpdatedAt { get; set; }

    // Staff bell: true while this row is in a low-stock episode (one alert per episode). Recovering to
    // the threshold closes it, so a later drop opens episode LowStockEpisode + 1.
    public bool LowStockAlertOpen { get; set; }
    public int LowStockEpisode { get; set; }

    // Navigation
    public ProductVariation? Variation { get; set; }
    public Location? Location { get; set; }
}
