using Api.Middlewares;

namespace Applications.Audit;

/// <summary>The actions the shared catalog accepts from POS. The business verb goes in the description and metadata.operation.</summary>
public static class AuditActions
{
    public const string Created = "record.created";
    public const string Updated = "record.updated";
    public const string Deleted = "record.deleted";
}

/// <summary>Who acted. Staff are identified by their Enterprise Portal user id, as Portal My logs expects.</summary>
public sealed record AuditActor(string Type, string Id, string DisplayName)
{
    /// <summary>
    /// The signed-in staff member, or null when the Portal did not verify them. Only the Portal session
    /// middleware sets <see cref="CurrentUserContext.PortalUserId"/>, so browser-supplied ids never qualify.
    /// </summary>
    public static AuditActor? Staff(CurrentUserContext? user)
    {
        var id = user?.PortalUserId?.Trim();
        var name = !string.IsNullOrWhiteSpace(user?.DisplayName) ? user!.DisplayName!.Trim() : user?.Username?.Trim();
        return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) ? null : new AuditActor("user", id, name);
    }

    /// <summary>A named background job.</summary>
    public static AuditActor Process(string id, string displayName) => new("process", id, displayName);
}

public sealed record AuditTarget(string Type, string Id, string DisplayName);

/// <summary>
/// One confirmed POS change, built by the code that owns it. The audit boundary resolves the actor,
/// adds the source and correlation, derives the idempotency key and delivers it.
/// </summary>
public sealed class AuditEvent
{
    /// <summary>Canonical POS Module; null for pages outside every Module (Starting Cash).</summary>
    public string? Module { get; init; }
    public required string Action { get; init; }
    public string Outcome { get; init; } = "success";

    /// <summary>Null means the signed-in staff member; an unverified one skips the event.</summary>
    public AuditActor? Actor { get; init; }
    public required AuditTarget Target { get; init; }

    /// <summary>Plain-language description, given the actor's display name.</summary>
    public required Func<string, string> Describe { get; init; }

    /// <summary>When the change happened (UTC), taken from the saved record where there is one.</summary>
    public required DateTime OccurredAt { get; init; }

    /// <summary>
    /// Internal identity of this occurrence, e.g. "order-history:7314". Null for an edit without a saved
    /// revision: the boundary then uses the request's Idempotency-Key header, or a new id per edit.
    /// </summary>
    public string? OperationId { get; init; }

    /// <summary>Allowlisted keys only (see <see cref="AuditMetadata.AllowedKeys"/>); never free text or customer data.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
}

public static class AuditMetadata
{
    /// <summary>Everything else is dropped by the boundary.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "operation",
        "location_id",
        "previous_status",
        "new_status",
        "partial",
        "payment_status",
        "order_type",
        "order_id",
        "product_id",
        "variation_id",
        "old_price",
        "new_price",
        "quantity",
        "adjustment_type",
        "changed_fields",
        "business_date",
        "auto_closed",
        "reconciliation",
        "downpayment",
        "previous_location_id",
    };
}

/// <summary>Shared wording for order events, so staff can find them by order number and branch.</summary>
public static class OrderAudit
{
    public static AuditTarget Target(int orderId, string orderNumber) =>
        new("order", orderId.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Order {orderNumber}");

    /// <summary>" at Makati Branch", or empty when the order has no branch.</summary>
    public static string AtBranch(string? locationName, int? locationId) =>
        !string.IsNullOrWhiteSpace(locationName) ? $" at {locationName}"
        : locationId.HasValue ? $" at Branch {locationId.Value}"
        : string.Empty;
}
