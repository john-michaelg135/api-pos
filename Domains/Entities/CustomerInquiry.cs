namespace Domains.Entities;

public class CustomerInquiry
{
    public int InquiryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? OrderReference { get; set; }
    
    // Status can be Pending, RoutedToCrms, Resolved, etc.
    public string Status { get; set; } = "Pending";
    
    public DateTime CreatedAt { get; set; }
}
