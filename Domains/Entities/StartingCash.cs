namespace Domains.Entities;

/// <summary>
/// The cash a cashier declares is in the drawer before they start selling.
/// One entry per cashier, per branch, per business day. Immutable once recorded
/// so the manager has a trustworthy record to compare against.
/// </summary>
public class StartingCash
{
    public int Id { get; set; }

    /// <summary>br-auth user id (OIDC "sub" GUID) of the cashier.</summary>
    public string AuthUserId { get; set; } = string.Empty;
    public string CashierName { get; set; } = string.Empty;

    public int LocationId { get; set; }

    /// <summary>Business date (UTC+8, Philippine time) the entry applies to.</summary>
    public DateOnly BusinessDate { get; set; }

    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal? EndShiftAmount { get; set; }
    public DateTime? EndShiftAt { get; set; }
    public string? EndShiftAuthUserId { get; set; }
    public string? EndShiftCashierName { get; set; }
    public decimal? SuccessfulSalesAmount { get; set; }
    public decimal? ApprovedRefundAmount { get; set; }
    public decimal? ExpectedCash { get; set; }

    /// <summary>
    /// True when the system closed the shift at the end of its business day because no
    /// cashier ended it; EndShiftAmount is then empty, as no cash was counted.
    /// </summary>
    public bool AutoClosed { get; set; }

    public Location? Location { get; set; }
}
