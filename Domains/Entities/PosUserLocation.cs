namespace Domains.Entities;

/// <summary>
/// Maps an Enterprise Portal user (identified by its opaque user id) to a POS
/// location. The Portal owns identity and POS owns location assignments.
///
/// Multiple rows per user are allowed so a manager can oversee several
/// branches. A cashier typically has exactly one row. Super admins/owners get
/// no rows at all — they are scoped to every location at runtime.
/// </summary>
public class PosUserLocation
{
    public int Id { get; set; }

    /// <summary>Enterprise Portal user id. Join key to the validated POS session.</summary>
    public string AuthUserId { get; set; } = string.Empty;

    /// <summary>FK to the assigned Location.</summary>
    public int LocationId { get; set; }

    /// <summary>Marks the user's primary/home branch when they cover several.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>Enterprise Portal user id of the admin who created the assignment.</summary>
    public string? AssignedBy { get; set; }

    public DateTime AssignedAt { get; set; }

    // Navigation
    public Location? Location { get; set; }
}
