namespace Api.Contracts.Scms;

/// <summary>An SCMS delivery waiting at a branch, as shown on POS Receive Stock.</summary>
public class PendingTransferDto
{
    public string TransferId { get; set; } = string.Empty;
    public string SourceLocationName { get; set; } = string.Empty;
    public int DestinationLocationId { get; set; }
    public string DestinationLocationName { get; set; } = string.Empty;
    public DateTime TransferDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<PendingTransferLineDto> Lines { get; set; } = new();
}

public class PendingTransferLineDto
{
    public int LineId { get; set; }
    public string? Sku { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int ExpectedQuantity { get; set; }

    // The POS variation this SKU matched; null when the SKU isn't in the POS catalog yet.
    public int? VariationId { get; set; }
    public string? VariationName { get; set; }
    public string? PosProductName { get; set; }
}

/// <summary>What the branch counted for each line of a delivery.</summary>
public class ReceiveTransferRequest
{
    public string? Notes { get; set; }
    public List<ReceiveTransferLineRequest> Lines { get; set; } = new();
}

public class ReceiveTransferLineRequest
{
    public int LineId { get; set; }
    public int ReceivedQuantity { get; set; }
    public int DamagedQuantity { get; set; }
    public string? DiscrepancyReason { get; set; }
}
