using Api.Contracts.Voucher;
using Applications.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VoucherController : ControllerBase
{
    private readonly IVoucherService _voucherService;

    public VoucherController(IVoucherService voucherService)
    {
        _voucherService = voucherService;
    }

    [HttpPost]
    public async Task<ActionResult<VoucherResponseDto>> CreateVoucher([FromBody] CreateVoucherDto dto)
    {
        try
        {
            var result = await _voucherService.CreateVoucherAsync(dto);
            return CreatedAtAction(nameof(GetVoucherById), new { id = result.VoucherId }, result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<ActionResult<List<VoucherResponseDto>>> GetAllVouchers([FromQuery] bool includeInactive = false)
    {
        var result = await _voucherService.GetAllVouchersAsync(includeInactive);
        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<VoucherResponseDto>> GetVoucherById(int id)
    {
        var result = await _voucherService.GetVoucherByIdAsync(id);
        if (result == null) return NotFound();

        return Ok(result);
    }

    [HttpGet("code/{code}")]
    public async Task<ActionResult<VoucherResponseDto>> GetVoucherByCode(string code)
    {
        var result = await _voucherService.GetVoucherByCodeAsync(code);
        if (result == null) return NotFound();

        return Ok(result);
    }

    [HttpPatch("{id:int}")]
    public async Task<ActionResult<VoucherResponseDto>> UpdateVoucher(int id, [FromBody] UpdateVoucherDto dto)
    {
        var result = await _voucherService.UpdateVoucherAsync(id, dto);
        if (result == null) return NotFound();

        return Ok(result);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteVoucher(int id)
    {
        var success = await _voucherService.DeleteVoucherAsync(id);
        if (!success) return NotFound();

        return NoContent();
    }
}
