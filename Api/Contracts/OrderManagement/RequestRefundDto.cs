namespace Api.Contracts.OrderManagement;

public class RequestRefundDto
{
    public string Reason { get; set; } = string.Empty;
    public string? ProofImage { get; set; } // data:image/...;base64,... shrunk in the browser

    // Items and quantities being returned. Leave empty to refund everything not yet refunded.
    public List<RefundItemDto>? Items { get; set; }
}

public class RefundItemDto
{
    public int VariationId { get; set; }
    public int Quantity { get; set; }
}
