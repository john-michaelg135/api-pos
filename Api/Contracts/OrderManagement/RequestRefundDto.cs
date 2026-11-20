namespace Api.Contracts.OrderManagement;

public class RequestRefundDto
{
    public string Reason { get; set; } = string.Empty;
    public string? ProofImage { get; set; } // Legacy: data:image/...;base64,... shrunk in the browser
    public string? ProofReference { get; set; } // Ready reference from proof-uploads/refund/confirm (send one or the other)

    // Items and quantities being returned. Leave empty to refund everything not yet refunded.
    public List<RefundItemDto>? Items { get; set; }
}

public class RefundItemDto
{
    public int VariationId { get; set; }
    public int Quantity { get; set; }
}
