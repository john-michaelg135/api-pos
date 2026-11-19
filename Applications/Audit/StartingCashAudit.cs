using System.Globalization;
using Domains.Entities;

namespace Applications.Audit;

/// <summary>
/// Starting Cash is role-gated and belongs to no Module, so these events omit <c>module</c>.
/// The saved starting-cash row identifies the drawer; it opens once and closes once.
/// </summary>
public static class StartingCashAudit
{
    public static AuditEvent Opened(StartingCash entry, string? branch) => new()
    {
        Action = AuditActions.Created,
        Target = Target(entry, branch),
        Describe = who => $"{who} opened the cash drawer{OrderAudit.AtBranch(branch, entry.LocationId)} for {Date(entry)}.",
        OccurredAt = entry.CreatedAt,
        OperationId = $"starting-cash:{entry.Id}",
        Metadata = Metadata(entry, "drawer_opened"),
    };

    /// <summary>A cashier's close (actor null: the signed-in staff member) or the end-of-day job's.</summary>
    public static AuditEvent Closed(StartingCash entry, string? branch, AuditActor? actor) => new()
    {
        Action = AuditActions.Updated,
        Actor = actor,
        Target = Target(entry, branch),
        Describe = who => entry.AutoClosed
            ? $"{who} closed the cash drawer{OrderAudit.AtBranch(branch, entry.LocationId)} for {Date(entry)} at the end of the business day."
            : $"{who} closed the cash drawer{OrderAudit.AtBranch(branch, entry.LocationId)} for {Date(entry)}.",
        OccurredAt = entry.EndShiftAt ?? DateTime.UtcNow,
        OperationId = $"starting-cash-closed:{entry.Id}",
        Metadata = Metadata(entry, entry.AutoClosed ? "drawer_auto_closed" : "drawer_closed"),
    };

    private static AuditTarget Target(StartingCash entry, string? branch) =>
        new("starting_cash", entry.Id.ToString(CultureInfo.InvariantCulture),
            $"Cash drawer{OrderAudit.AtBranch(branch, entry.LocationId)} on {Date(entry)}");

    private static Dictionary<string, object?> Metadata(StartingCash entry, string operation) => new()
    {
        ["operation"] = operation,
        ["location_id"] = entry.LocationId,
        ["business_date"] = Date(entry),
        ["auto_closed"] = entry.AutoClosed ? true : null,
    };

    private static string Date(StartingCash entry) => entry.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
