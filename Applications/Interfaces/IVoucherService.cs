using Api.Contracts.Voucher;

namespace Applications.Interfaces;

public interface IVoucherService
{
    Task<VoucherResponseDto> CreateVoucherAsync(CreateVoucherDto dto);
    Task<List<VoucherResponseDto>> GetAllVouchersAsync(bool includeInactive = false);
    Task<VoucherResponseDto?> GetVoucherByIdAsync(int voucherId);
    Task<VoucherResponseDto?> GetVoucherByCodeAsync(string voucherCode);
    Task<VoucherResponseDto?> UpdateVoucherAsync(int voucherId, UpdateVoucherDto dto);
    Task<bool> DeleteVoucherAsync(int voucherId); // Soft delete
}
