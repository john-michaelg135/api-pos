namespace Api.Contracts.Voucher;

public class VoucherResponseDto
{
    public int VoucherId { get; set; }
    public string VoucherCode { get; set; } = string.Empty;
    public string DiscountType { get; set; } = string.Empty;
    public decimal DiscountValue { get; set; }
    public decimal MinimumSpend { get; set; }
    public bool IsActive { get; set; }
    public DateTime ExpiryDate { get; set; }
    public DateTime CreatedAt { get; set; }
}
