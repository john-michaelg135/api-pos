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

    public Location? Location { get; set; }
}
