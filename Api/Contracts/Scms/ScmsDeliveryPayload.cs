namespace Api.Contracts.Scms;

/// <summary>Payload shape expected from the SCMS group's bulk deliveries API.</summary>
public class ScmsDeliveryPayload
{
    public List<ScmsDeliveryItem> Deliveries { get; set; } = new();
}

public class ScmsDeliveryItem
{
    /// <summary>SCMS's own product identifier — stored for cross-reference.</summary>
    public string? ScmsProductId { get; set; }

    /// <summary>POS VariationId that maps to this SCMS product.</summary>
    public int VariationId { get; set; }

    /// <summary>POS LocationId where the goods are being delivered.</summary>
    public int LocationId { get; set; }

    public int QuantityDelivered { get; set; }
    public string? DeliveryReference { get; set; }
    public string? Notes { get; set; }
}
