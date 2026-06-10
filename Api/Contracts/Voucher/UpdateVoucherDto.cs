using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.Voucher;

public class UpdateVoucherDto
{
    public bool? IsActive { get; set; }
    public DateTime? ExpiryDate { get; set; }

    [Range(0, 1000000.0)]
    public decimal? MinimumSpend { get; set; }
}
