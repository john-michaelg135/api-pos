namespace Api.Contracts.Analytics;

/// <summary>
/// Company sales report for a period: a sales summary (gross → discounts → returns → net),
/// the same figures for the previous period of equal length, and breakdowns by branch,
/// channel, payment method, product and day.
/// Sales are recognised on the day the order was placed; returns on the day the refund was approved.
/// </summary>
public class SalesReportDto
{
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public DateTime? PreviousFrom { get; set; }
    public DateTime? PreviousTo { get; set; }
    public DateTime GeneratedAt { get; set; }

    public SalesReportTotalsDto Current { get; set; } = new();
    /// <summary>Null when the report has no start date, so there is no equal-length period to compare.</summary>
    public SalesReportTotalsDto? Previous { get; set; }

    public List<SalesReportLineDto> ByBranch { get; set; } = new();
    public List<SalesReportLineDto> ByChannel { get; set; } = new();
    public List<SalesReportLineDto> ByPaymentMethod { get; set; } = new();
    public List<SalesReportProductDto> ByProduct { get; set; } = new();

    /// <summary>"day" when the period spans up to 62 days, otherwise "month".</summary>
    public string TrendGranularity { get; set; } = "day";
    public List<SalesReportLineDto> Trend { get; set; } = new();
}

public class SalesReportTotalsDto
{
    /// <summary>Sales at list price, before discounts.</summary>
    public decimal GrossSales { get; set; }
    /// <summary>Senior Citizen / PWD discounts (20% plus the 12% VAT exemption).</summary>
    public decimal Discounts { get; set; }
    /// <summary>Approved refunds (sales returns) in the period.</summary>
    public decimal Returns { get; set; }
    public decimal NetSales { get; set; }
    public int Transactions { get; set; }
    public int UnitsSold { get; set; }
    public int RefundCount { get; set; }
    /// <summary>Amount charged per transaction (gross less discounts, over transactions).</summary>
    public decimal AverageTransaction { get; set; }
}

public class SalesReportLineDto : SalesReportTotalsDto
{
    public string Label { get; set; } = string.Empty;
    /// <summary>Share of the report's net sales, in percent.</summary>
    public decimal Share { get; set; }
}

public class SalesReportProductDto
{
    public string Sku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string VariationName { get; set; } = string.Empty;
    public int UnitsSold { get; set; }
    public int UnitsReturned { get; set; }
    public decimal GrossSales { get; set; }
    public decimal Discounts { get; set; }
    public decimal Returns { get; set; }
    public decimal NetSales { get; set; }
    /// <summary>Average price actually charged per unit sold.</summary>
    public decimal AverageUnitPrice { get; set; }
    /// <summary>Share of the report's gross sales (sales mix), in percent.</summary>
    public decimal Share { get; set; }
}
