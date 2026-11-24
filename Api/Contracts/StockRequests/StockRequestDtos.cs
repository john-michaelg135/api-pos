namespace Api.Contracts.StockRequests;

/// <summary>Branch UI → POS: ask IMS for more of one variation (STOCK-REQUEST-INTEGRATION.md §3.5).</summary>
public class CreateStockRequestDto
{
    public int VariationId { get; set; }
    public int LocationId { get; set; }
    public int Quantity { get; set; }
    public string Notes { get; set; } = string.Empty;
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
    public string? Remarks { get; set; }
    /// <summary>Set while still Sending after a failed attempt, so the UI can say IMS couldn't be reached.</summary>
    public string? LastSendError { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? TransferId { get; set; }
    public DateTime? FulfilledAt { get; set; }
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
