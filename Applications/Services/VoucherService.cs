using Api.Contracts.Voucher;
using Applications.Interfaces;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class VoucherService : IVoucherService
{
    private readonly PosDbContext _context;

    public VoucherService(PosDbContext context)
    {
        _context = context;
    }

    public async Task<VoucherResponseDto> CreateVoucherAsync(CreateVoucherDto dto)
    {
        // Check if voucher code already exists
        var existingVoucher = await _context.Vouchers
            .FirstOrDefaultAsync(v => v.VoucherCode == dto.VoucherCode);

        if (existingVoucher != null)
        {
            throw new Exception($"Voucher with code '{dto.VoucherCode}' already exists.");
        }

        var voucher = new Voucher
        {
            VoucherCode = dto.VoucherCode,
            DiscountType = dto.DiscountType,
            DiscountValue = dto.DiscountValue,
            MinimumSpend = dto.MinimumSpend,
            ExpiryDate = dto.ExpiryDate,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Vouchers.Add(voucher);
        await _context.SaveChangesAsync();

        return MapToDto(voucher);
    }

    public async Task<List<VoucherResponseDto>> GetAllVouchersAsync(bool includeInactive = false)
    {
        var query = _context.Vouchers.AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(v => v.IsActive);
        }

        var vouchers = await query.ToListAsync();

        return vouchers.Select(MapToDto).ToList();
    }

    public async Task<VoucherResponseDto?> GetVoucherByIdAsync(int voucherId)
    {
        var voucher = await _context.Vouchers.FindAsync(voucherId);
        if (voucher == null) return null;

        return MapToDto(voucher);
    }

    public async Task<VoucherResponseDto?> GetVoucherByCodeAsync(string voucherCode)
    {
        var voucher = await _context.Vouchers
            .FirstOrDefaultAsync(v => v.VoucherCode == voucherCode);
            
        if (voucher == null) return null;

        return MapToDto(voucher);
    }

    public async Task<VoucherResponseDto?> UpdateVoucherAsync(int voucherId, UpdateVoucherDto dto)
    {
        var voucher = await _context.Vouchers.FindAsync(voucherId);
        if (voucher == null) return null;

        if (dto.IsActive.HasValue)
        {
            voucher.IsActive = dto.IsActive.Value;
        }

        if (dto.ExpiryDate.HasValue)
        {
            voucher.ExpiryDate = dto.ExpiryDate.Value;
        }

        if (dto.MinimumSpend.HasValue)
        {
            voucher.MinimumSpend = dto.MinimumSpend.Value;
        }

        await _context.SaveChangesAsync();

        return MapToDto(voucher);
    }

    public async Task<bool> DeleteVoucherAsync(int voucherId)
    {
        var voucher = await _context.Vouchers.FindAsync(voucherId);
        if (voucher == null) return false;

        voucher.IsActive = false; // Soft delete
        await _context.SaveChangesAsync();

        return true;
    }

    private static VoucherResponseDto MapToDto(Voucher voucher)
    {
        return new VoucherResponseDto
        {
            VoucherId = voucher.VoucherId,
            VoucherCode = voucher.VoucherCode,
            DiscountType = voucher.DiscountType,
            DiscountValue = voucher.DiscountValue,
            MinimumSpend = voucher.MinimumSpend,
            IsActive = voucher.IsActive,
            ExpiryDate = voucher.ExpiryDate,
            CreatedAt = voucher.CreatedAt
        };
    }
}
