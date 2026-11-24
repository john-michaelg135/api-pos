namespace Api.Contracts.Scms;

/// <summary>POS → IMS: POST /api/pos/stock-requests (STOCK-REQUEST-INTEGRATION.md §3.1).</summary>
public class StockRequestDto
{
    public string RequestId { get; set; } = string.Empty;
    public DestinationBranchDto Branch { get; set; } = new();
    public List<StockRequestItemDto> Items { get; set; } = new();
    public string Notes { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string RequestedByName { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
}

public class StockRequestItemDto
{
    public string Sku { get; set; } = string.Empty;
    public string? ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string VariationName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int CurrentStock { get; set; }
}

/// <summary>POS → IMS: PUT /api/pos/stock-requests/{requestId}/cancel (STOCK-REQUEST-INTEGRATION.md §3.6).</summary>
public class StockRequestCancelDto
{
    public string RequestId { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string CancelledBy { get; set; } = string.Empty;
    public string CancelledByName { get; set; } = string.Empty;
    public DateTime CancelledAt { get; set; }
}

/// <summary>What IMS said to a cancel. NotFound = IMS never had it; Refused = IMS already decided (409).</summary>
public enum StockRequestCancelOutcome
{
    Cancelled,
    NotFound,
    Refused,
    Unreachable,
}

public sealed record StockRequestCancelResult(StockRequestCancelOutcome Outcome, string? Message);

/// <summary>What IMS said to a submit. Accepted = 200/201, Refused = 4xx with a reason, Unreachable = retry later.</summary>
public enum StockRequestSubmitOutcome
{
    Accepted,
    Refused,
    Unreachable,
}

public sealed record StockRequestSubmitResult(StockRequestSubmitOutcome Outcome, string? ImsRequestId, string? Message);
