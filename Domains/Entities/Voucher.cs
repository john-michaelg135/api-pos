namespace Domains.Entities;

public class Voucher
{
    public int VoucherId { get; set; }
    public string VoucherCode { get; set; } = string.Empty;
    public string DiscountType { get; set; } = "Percentage"; // Percentage or Fixed
    public decimal DiscountValue { get; set; }
    public decimal MinimumSpend { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    public DateTime ExpiryDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
