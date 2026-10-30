namespace Api.Contracts.OrderManagement;

public class RequestRefundDto
{
    public string Reason { get; set; } = string.Empty;
    public string? ProofImage { get; set; } // data:image/...;base64,... shrunk in the browser
}
