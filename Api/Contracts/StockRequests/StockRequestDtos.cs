namespace Api.Contracts.StockRequests;

/// <summary>Branch UI → POS: ask IMS for more of one variation (STOCK-REQUEST-INTEGRATION.md §3.5).</summary>
public class CreateStockRequestDto
{
    public int VariationId { get; set; }
    public int LocationId { get; set; }
    public int Quantity { get; set; }
    public string Notes { get; set; } = string.Empty;
    /// <summary>Optional, generated once per form: the same key returns the same request (no duplicate on a double submit).</summary>
    public string? ClientRequestId { get; set; }
}

/// <summary>Branch UI → POS: withdraw a request IMS hasn't decided on.</summary>
public class CancelStockRequestDto
{
    public string? Reason { get; set; }
}

/// <summary>POS → branch UI.</summary>
public class StockRequestResponseDto
{
    public string RequestId { get; set; } = string.Empty;
    public int VariationId { get; set; }
    public string VariationName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int? ApprovedQuantity { get; set; }
    public int? ReceivedQuantity { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    /// <summary>True while IMS hasn't decided (Sending or Submitted).</summary>
    public bool CanCancel { get; set; }
    public string? Remarks { get; set; }
    /// <summary>IMS refused the submit itself (e.g. an unknown SKU) rather than deciding on the request.</summary>
    public bool RefusedOnSubmit { get; set; }
    /// <summary>Set while still Sending after a failed attempt, so the UI can say IMS couldn't be reached.</summary>
    public string? LastSendError { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    /// <summary>When IMS acknowledged it.</summary>
    public DateTime? SentAt { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? TransferId { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? FulfilledAt { get; set; }
    public string? CancelledByName { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
}

/// <summary>IMS → POS: approve or reject (§3.2).</summary>
public class StockRequestDecisionDto
{
    public string Status { get; set; } = string.Empty;
    public int? ApprovedQuantity { get; set; }
    public string? Remarks { get; set; }
    public string? ImsRequestId { get; set; }
    public string DecidedBy { get; set; } = string.Empty;
    public string DecidedByName { get; set; } = string.Empty;
    public DateTime DecidedAt { get; set; }
}
