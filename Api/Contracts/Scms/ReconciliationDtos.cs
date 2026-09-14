namespace Api.Contracts.Scms;

/// <summary>
/// Top-level reconciliation report comparing SCM transfer manifests against
/// what POS actually received into stock. Produced by ReconciliationService.
/// </summary>
public class ReconciliationReportDto
{
    public DateTime GeneratedAt { get; set; }

    // Roll-up counters across all transfers examined.
    public int TotalTransfers { get; set; }
    public int MatchedTransfers { get; set; }
    public int DiscrepancyTransfers { get; set; }
    public int UnreceivedTransfers { get; set; }      // manifest exists, no receipt recorded
    public int PendingStatusCallbacks { get; set; }   // received in POS but SCM never acknowledged

    public List<TransferReconciliationDto> Transfers { get; set; } = new();
}

/// <summary>Per-transfer reconciliation detail.</summary>
public class TransferReconciliationDto
{
    public string TransferId { get; set; } = string.Empty;
    public string DestinationBranchId { get; set; } = string.Empty;
    public string DestinationBranchName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;             // transfer lifecycle status
    public string StatusSyncState { get; set; } = string.Empty;    // Pending / Acknowledged / Failed
    public string ReconciliationState { get; set; } = string.Empty; // Unreconciled / Matched / Discrepancy
    public DateTime? ReceivedAt { get; set; }
    public DateTime? ReconciledAt { get; set; }

    public int TotalExpected { get; set; }
    public int TotalReceived { get; set; }
    public int TotalDamaged { get; set; }

    // Human-readable issues found on this transfer (empty when clean).
    public List<string> Issues { get; set; } = new();

    public List<LineReconciliationDto> Lines { get; set; } = new();
}

/// <summary>Per-line reconciliation detail within a transfer.</summary>
public class LineReconciliationDto
{
    public string ProductId { get; set; } = string.Empty;   // SCM product id
    public string? Sku { get; set; }                        // canonical SKU used for matching
    public string ProductName { get; set; } = string.Empty;
    public int? ResolvedVariationId { get; set; }           // null = unmatched in POS catalog

    public int ExpectedQuantity { get; set; }
    public int? ReceivedQuantity { get; set; }              // null = not yet received
    public int DamagedQuantity { get; set; }
    public int Variance { get; set; }                       // Received - Expected (negative = short)

    // Matched, Unmatched (no POS variation), Short, Over, NotReceived
    public string LineState { get; set; } = string.Empty;
}

/// <summary>
/// Result of asking POS to heal a single transfer (re-match SKUs, re-run the
/// reconciliation verdict, and re-attempt the SCM status callback if needed).
/// </summary>
public class ReconciliationResyncResultDto
{
    public string TransferId { get; set; } = string.Empty;
    public bool StatusCallbackResent { get; set; }
    public bool StatusCallbackAcknowledged { get; set; }
    public int LinesRematched { get; set; }
    public string ReconciliationState { get; set; } = string.Empty;
    public List<string> Messages { get; set; } = new();
}
