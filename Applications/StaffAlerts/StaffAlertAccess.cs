using Domains.Entities;

namespace Applications.StaffAlerts;

/// <summary>A person who might see an alert: Portal ID, role, Portal Modules granted now, POS branches.</summary>
public sealed record StaffViewer(
    string PortalUserId,
    string? Role,
    IReadOnlyCollection<string> Modules,
    IReadOnlyCollection<int> LocationIds);

/// <summary>
/// Who may see (and receive a Portal copy of) which alert. The same rules serve the POS feed, its
/// counts and marks, and recipient resolution, so a Portal copy never reaches someone the POS bell
/// would hide the alert from. Roles mirror PortalSessionMiddleware and PosRoleAuthorization.
/// </summary>
public static class StaffAlertAccess
{
    public const string OrderManagementModule = "Order Management";
    public const string StockManagementModule = "Stock Management";

    public static readonly string[] BellModules = [OrderManagementModule, StockManagementModule];

    private static readonly HashSet<string> AdministratorRoles = new(StringComparer.Ordinal) { "Super Admin", "CEO" };
    private const string GeneralManagerRole = "General Manager";

    /// <summary>Every branch, within the Modules they hold.</summary>
    public static readonly HashSet<string> AllLocationRoles = new(StringComparer.Ordinal) { "Super Admin", "CEO", GeneralManagerRole };

    /// <summary>May approve refunds/voids and receive stock (PosRoleAuthorization.RequireManager).</summary>
    public static bool IsActor(string? role) =>
        role is not null && (AllLocationRoles.Contains(role) || role is "POS Manager" or "Manager");

    public static bool RoleMayUseModule(string? role, string module)
    {
        if (role is null) return false;
        if (AdministratorRoles.Contains(role)) return true;
        return role switch
        {
            "Cashier" => module is OrderManagementModule,
            "POS Manager" or "Manager" => module is OrderManagementModule or StockManagementModule,
            GeneralManagerRole => true,
            _ => false,
        };
    }

    /// <summary>Bell Modules this person holds and whose work their role may do.</summary>
    public static string[] UsableModules(string? role, IEnumerable<string> granted) =>
        BellModules.Where(m => granted.Contains(m, StringComparer.Ordinal) && RoleMayUseModule(role, m)).ToArray();

    public static bool CoversLocation(StaffViewer viewer, int? locationId) =>
        (viewer.Role is not null && AllLocationRoles.Contains(viewer.Role)) ||
        (locationId.HasValue && viewer.LocationIds.Contains(locationId.Value));

    public static bool CanSee(StaffAlert alert, StaffViewer viewer)
    {
        if (string.IsNullOrEmpty(viewer.PortalUserId)) return false;
        if (!UsableModules(viewer.Role, viewer.Modules).Contains(alert.Module, StringComparer.Ordinal)) return false;
        if (!CoversLocation(viewer, alert.LocationId)) return false;

        if (alert.Audience == StaffAlertAudiences.Requester)
            return alert.RecipientPortalUserId is { Length: > 0 } recipient &&
                   string.Equals(recipient, viewer.PortalUserId, StringComparison.Ordinal);

        return IsActor(viewer.Role) &&
               !string.Equals(alert.RequesterPortalUserId, viewer.PortalUserId, StringComparison.Ordinal);
    }

    /// <summary><see cref="CanSee"/> as a query, for lists, counts and marks.</summary>
    public static IQueryable<StaffAlert> VisibleTo(IQueryable<StaffAlert> alerts, StaffViewer viewer)
    {
        var modules = UsableModules(viewer.Role, viewer.Modules);
        if (string.IsNullOrEmpty(viewer.PortalUserId) || modules.Length == 0) return alerts.Where(_ => false);

        var userId = viewer.PortalUserId;
        var everyBranch = viewer.Role is not null && AllLocationRoles.Contains(viewer.Role);
        var branches = viewer.LocationIds.ToArray();
        var actor = IsActor(viewer.Role);

        var query = alerts.Where(a => modules.Contains(a.Module));
        if (!everyBranch)
            query = query.Where(a => a.LocationId != null && branches.Contains(a.LocationId.Value));

        return actor
            ? query.Where(a =>
                (a.Audience == StaffAlertAudiences.Requester && a.RecipientPortalUserId == userId) ||
                (a.Audience == StaffAlertAudiences.Branch && (a.RequesterPortalUserId == null || a.RequesterPortalUserId != userId)))
            : query.Where(a => a.Audience == StaffAlertAudiences.Requester && a.RecipientPortalUserId == userId);
    }
}
