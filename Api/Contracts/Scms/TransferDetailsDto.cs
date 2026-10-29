namespace Api.Contracts.Scms;

public class TransferDetailsDto
{
    public string TransferId { get; set; } = string.Empty;
    public LocationDto SourceLocation { get; set; } = new();
    public DestinationBranchDto DestinationLocation { get; set; } = new();
    public List<TransferProductDto> Products { get; set; } = new();
    public DateTime TransferDate { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class LocationDto
{
    public string LocationId { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
}

public class DestinationBranchDto
{
    public string BranchId { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
}

public class TransferProductDto
{
    public string ProductId { get; set; } = string.Empty;
    /// <summary>Canonical SKU shared with SCMS; how the line is matched to a POS variation on receipt.</summary>
    public string? Sku { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    /// <summary>Production lot (optional; accepted per the spec, not stored yet).</summary>
    public string? BatchNumber { get; set; }
    /// <summary>Shelf-life expiry (optional; accepted per the spec, not stored yet).</summary>
    public DateTime? ExpirationDate { get; set; }
}
