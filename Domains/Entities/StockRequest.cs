namespace Domains.Entities;

/// <summary>
/// A branch asking IMS (SCMS in code) for more of one variation. POS never approves it: IMS
/// decides, ships it as a normal transfer manifest, and the branch's Receive Stock closes it.
/// See STOCK-REQUEST-INTEGRATION.md.
/// </summary>
public class StockRequest
{
    public int Id { get; set; }

    // "SR-000123"; the id IMS sees and dedupes on.
    public string RequestId { get; set; } = string.Empty;

    public int VariationId { get; set; }
    public int LocationId { get; set; }

    // Snapshots at request time, so later catalog edits don't rewrite what was asked for.
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int CurrentStockAtRequest { get; set; }
    public string Notes { get; set; } = string.Empty;

    public string Status { get; set; } = StockRequestStatuses.Sending;

    public int? ApprovedQuantity { get; set; }
    public int? ReceivedQuantity { get; set; }
    // IMS's reason on a reject, or IMS's 400 message when it refused the submit.
    public string? Remarks { get; set; }
    public string? ImsRequestId { get; set; }

    public string RequestedBy { get; set; } = string.Empty;
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }

    // Delivery to IMS (the retry sweep re-sends while Status is Sending).
    public DateTime? SentAt { get; set; }
    public int SendAttempts { get; set; }
    public string? LastSendError { get; set; }

    public string? DecidedBy { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }

    // The IMS delivery that fulfils it (set when the manifest arrives).
    public string? TransferId { get; set; }
    public DateTime? FulfilledAt { get; set; }

    public ProductVariation? Variation { get; set; }
    public Location? Location { get; set; }
}

public static class StockRequestStatuses
{
    public const string Sending = "Sending";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string InTransit = "In Transit";
    public const string Fulfilled = "Fulfilled";
    public const string Cancelled = "Cancelled";

    /// <summary>Statuses that block a second request for the same variation at the same branch.</summary>
    public static readonly string[] Open = [Sending, Submitted, Approved, InTransit];
}
