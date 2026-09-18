namespace Domains.Entities;

/// <summary>
/// Maps a br-auth user (identified by their auth GUID / OIDC "sub") to a POS
/// location. br-auth owns identity (role, permissions) but does NOT assign
/// locations; this POS-owned table supplies that missing link.
///
/// Multiple rows per user are allowed so a manager can oversee several
/// branches. A cashier typically has exactly one row. Super admins/owners get
/// no rows at all — they are scoped to every location at runtime.
/// </summary>
public class PosUserLocation
{
    public int Id { get; set; }

    /// <summary>br-auth user id (OIDC "sub" GUID). Join key to the session.</summary>
    public string AuthUserId { get; set; } = string.Empty;

    /// <summary>FK to the assigned Location.</summary>
    public int LocationId { get; set; }

    /// <summary>Marks the user's primary/home branch when they cover several.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>br-auth user id of the admin who created the assignment (audit).</summary>
    public string? AssignedBy { get; set; }

    public DateTime AssignedAt { get; set; }

    // Navigation
    public Location? Location { get; set; }
}
