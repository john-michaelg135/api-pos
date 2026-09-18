namespace Api.Contracts.Customer;

public class RefundRequestDto
{
    public string Reason { get; set; } = string.Empty;
}

public class RefundResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    public static RefundResult Ok(string message) => new() { Success = true, Message = message };
    public static RefundResult Fail(string message) => new() { Success = false, Message = message };
}
