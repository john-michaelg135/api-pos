using System;

namespace Domains.Entities;

/// <summary>
/// One occurrence of staff work for the POS bell (refund/void request or decision, an SCMS delivery
/// to receive, a variation falling below its branch minimum). Saved in the same commit as the
/// business change; <see cref="OccurrenceKey"/> is unique, so a replay never makes a second alert.
///
/// Branch alerts are shared work: any eligible reviewer reading it marks it read for everyone.
/// Requester alerts (decisions) belong only to <see cref="RecipientPortalUserId"/>.
/// </summary>
public class StaffAlert
{
    public long Id { get; set; }

    /// <summary>"order-history:{id}", "transfer-arrived:{id}" or "low-stock:{location}:{variation}:{episode}".</summary>
    public string OccurrenceKey { get; set; } = string.Empty;

    /// <summary>One of <c>StaffAlertKinds</c>, e.g. "pos.refund-requested".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Portal Module a viewer needs: "Order Management" or "Stock Management".</summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>"branch" (eligible staff at <see cref="LocationId"/>) or "requester" (one person).</summary>
    public string Audience { get; set; } = StaffAlertAudiences.Branch;

    public int? LocationId { get; set; }
    public int? OrderId { get; set; }
    public string? TransferId { get; set; }
    public int? VariationId { get; set; }

    /// <summary>The order status-history row this alert was raised for.</summary>
    public int? SourceHistoryId { get; set; }

    /// <summary>Requester alerts: the only person who may see it.</summary>
    public string? RecipientPortalUserId { get; set; }

    /// <summary>Branch alerts: the person who filed the request (never asked to approve their own).</summary>
    public string? RequesterPortalUserId { get; set; }

    public bool Partial { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>Relative POS web path that opens the work, e.g. "/order-management?orderId=4821".</summary>
    public string LinkPath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Shared read state (branch alerts) or the requester's own (requester alerts).</summary>
    public DateTime? ReadAt { get; set; }
    public string? ReadByPortalUserId { get; set; }

    /// <summary>When the Portal audience was snapshotted (even an empty one). Null = not yet resolved.</summary>
    public DateTime? AudienceResolvedAt { get; set; }

    /// <summary>When the content-free refresh notice for this alert reached the POS staff channel.</summary>
    public DateTime? NudgedAt { get; set; }

    public ICollection<StaffAlertCopy> Copies { get; set; } = new List<StaffAlertCopy>();
}

/// <summary>
/// The Portal copy of one <see cref="StaffAlert"/> for one recipient. The remote notification ID is
/// kept so retries recover the same copy and read state can be synchronized both ways.
/// </summary>
public class StaffAlertCopy
{
    public long Id { get; set; }
    public long AlertId { get; set; }
    public string PortalUserId { get; set; } = string.Empty;

    /// <summary>Notification-service UUID once the copy is known to exist.</summary>
    public Guid? RemoteId { get; set; }

    public string State { get; set; } = StaffAlertCopyStates.Pending;

    /// <summary>True once nothing more is owed for this copy (read propagated, withheld or abandoned).</summary>
    public bool Done { get; set; }

    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>Error category only; never a key, ticket or response body.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }

    public StaffAlert? Alert { get; set; }
}

public static class StaffAlertAudiences
{
    public const string Branch = "branch";
    public const string Requester = "requester";
}

public static class StaffAlertCopyStates
{
    /// <summary>Not yet created remotely.</summary>
    public const string Pending = "Pending";
    /// <summary>Exists remotely (<see cref="StaffAlertCopy.RemoteId"/> set).</summary>
    public const string Sent = "Sent";
    /// <summary>The recipient lost access before delivery and no earlier copy existed.</summary>
    public const string Withheld = "Withheld";
    /// <summary>Already read in POS before delivery; no copy was created.</summary>
    public const string Skipped = "Skipped";
    /// <summary>Retries ran out.</summary>
    public const string Failed = "Failed";
}
