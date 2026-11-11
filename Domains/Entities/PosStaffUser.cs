namespace Domains.Entities;

/// <summary>
/// Gives each signed-in POS user a stable number, so the integer "who did it" columns
/// (RefundRequests.RequestedBy/ApprovedBy, Orders.ApprovedBy, OrderStatusHistories.ChangedBy)
/// record a real person. Rows are created the first time a user acts. Numbers start at 1000
/// so they can't be mistaken for the old placeholder 1 (or 0, "unknown / customer").
/// </summary>
public class PosStaffUser
{
    public int Id { get; set; }

    /// <summary>br-auth user id (portal user id, or the OIDC "sub" GUID), same key as PosUserPins.</summary>
    public string AuthUserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
