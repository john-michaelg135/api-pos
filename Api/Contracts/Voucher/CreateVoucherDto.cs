using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.Voucher;

public class CreateVoucherDto
{
    [Required]
    [StringLength(15, MinimumLength = 5, ErrorMessage = "Voucher code must be between 5 and 15 characters.")]
    [RegularExpression(@"^[a-zA-Z0-9\-_]+$", ErrorMessage = "Invalid voucher code format.")]
    public string VoucherCode { get; set; } = string.Empty;
    
    public string DiscountType { get; set; } = "Percentage"; // Percentage or Fixed
    
    [Range(0, 1000000.0)]
    public decimal DiscountValue { get; set; }
    
    [Range(0, 1000000.0)]
    public decimal MinimumSpend { get; set; } = 0;
    
    public DateTime ExpiryDate { get; set; }
}
