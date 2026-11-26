namespace Api.Contracts.Dashboard;

/// <summary>
/// Everything the owner's dashboard shows, in one response. Money figures are
/// "money collected": successful payments minus approved refunds, in Philippine time.
/// </summary>
public class OwnerDashboardDto
{
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    /// <summary>The period of equal length just before; null for "all dates", which has nothing to compare to.</summary>
    public DateOnly? PreviousDateFrom { get; set; }
    public DateOnly? PreviousDateTo { get; set; }
    public string Granularity { get; set; } = "day";      // trend points: hour | day | month
    public int? LocationId { get; set; }
    public DateTime GeneratedAt { get; set; }

    public DashboardKpiDto Sales { get; set; } = new();
    public DashboardKpiDto Orders { get; set; } = new();
    public DashboardKpiDto AverageSale { get; set; } = new();
    public DashboardKpiDto ItemsSold { get; set; } = new();

    public List<DashboardTrendPointDto> Trend { get; set; } = new();
    public List<DashboardBranchSalesDto> SalesByBranch { get; set; } = new();
    public List<DashboardTopProductDto> TopProducts { get; set; } = new();
    public List<DashboardChannelSalesDto> SalesByChannel { get; set; } = new();
    public List<DashboardBranchTodayDto> BranchesToday { get; set; } = new();
}

public class DashboardKpiDto
{
    public decimal Current { get; set; }
    public decimal Previous { get; set; }
}

public class DashboardTrendPointDto
{
    public string Label { get; set; } = string.Empty;    // "9 AM", "Oct 26" or "Oct", by granularity
    public decimal Sales { get; set; }
    public decimal PreviousSales { get; set; }
    public bool IsFuture { get; set; }                   // later today / later this period — not yet happened
}

public class DashboardBranchSalesDto
{
    public int LocationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Sales { get; set; }
    public int Orders { get; set; }
}

public class DashboardTopProductDto
{
    public string ProductName { get; set; } = string.Empty;
    public string VariationName { get; set; } = string.Empty;
    public int UnitsSold { get; set; }
    public decimal Sales { get; set; }
}

public class DashboardChannelSalesDto
{
    public string Channel { get; set; } = string.Empty;  // Walk-in | Institutional | Online
    public decimal Sales { get; set; }
    public int Orders { get; set; }
}

public class DashboardBranchTodayDto
{
    public int LocationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ShiftStatus { get; set; } = "Not opened"; // Not opened | Open | Closed
    public decimal SalesToday { get; set; }
    /// <summary>Counted minus expected cash across today's closed shifts; null until a shift is closed.</summary>
    public decimal? CashOverShort { get; set; }
}
