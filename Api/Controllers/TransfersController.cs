using Microsoft.AspNetCore.Mvc;
using Api.Contracts.Scms;
using Api.Middlewares;
using Applications.Services;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/transfers")]
public class TransfersController : ControllerBase
{
    private readonly PosDbContext _db;

    public TransfersController(PosDbContext db)
    {
        _db = db;
    }

    // POST api-pos/transfers
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReceiveTransferDetails([FromBody] TransferDetailsDto dto)
    {
        if (await ValidateManifestAsync(dto) is { } problem)
        {
            return BadRequest(new { success = false, message = problem });
        }

        // Check if it already exists
        var existing = await _db.StockTransfers.FindAsync(dto.TransferId);
        if (existing != null)
        {
            return Ok(new
            {
                success = true,
                message = "Transfer details already received.",
                data = new
                {
                    transferId = existing.TransferId,
                    status = existing.Status
                }
            });
        }

        var transfer = new StockTransfer
        {
            TransferId = dto.TransferId,
            SourceLocationId = dto.SourceLocation.LocationId,
            SourceLocationName = dto.SourceLocation.LocationName,
            DestinationBranchId = dto.DestinationLocation.BranchId,
            DestinationBranchName = dto.DestinationLocation.BranchName,
            TransferDate = dto.TransferDate == default ? DateTime.UtcNow : dto.TransferDate,
            // The spec's two opening states; anything else starts as Pending Receiving.
            Status = dto.Status is "In Transit" or "Pending Receiving" ? dto.Status : "Pending Receiving",
            Items = dto.Products.Select(p => new StockTransferItem
            {
                ProductId = p.ProductId,
                Sku = p.Sku?.Trim(),
                ProductName = p.ProductName,
                Quantity = p.Quantity
            }).ToList()
        };

        _db.StockTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "Transfer details received successfully.",
            data = new
            {
                transferId = transfer.TransferId,
                status = "Pending Receiving"
            }
        });
    }

    // GET api-pos/transfers/pending — deliveries waiting at the caller's branches (Receive Stock).
    [HttpGet("pending")]
    [ProducesResponseType(typeof(List<PendingTransferDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPending([FromServices] TransferReceivingService receiving)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        return Ok(await receiving.GetPendingAsync());
    }

    // POST api-pos/transfers/{transferId}/receive — the branch's count for each line.
    [HttpPost("{transferId}/receive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReceiveTransfer(string transferId, [FromServices] TransferReceivingService receiving, [FromBody] ReceiveTransferRequest request)
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;
        try
        {
            var message = await receiving.ReceiveAsync(transferId, request);
            return Ok(new { success = true, message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Checks an SCMS manifest against SCMS-POS-INTEGRATION-SPEC.md (Module 2) before anything
    /// is saved; returns the first problem, or null when it can be stored.
    /// </summary>
    private async Task<string?> ValidateManifestAsync(TransferDetailsDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.TransferId))
            return "transferId is required.";
        if (dto.TransferId.Length > 50)
            return "transferId must be 50 characters or fewer.";

        if (!int.TryParse(dto.DestinationLocation?.BranchId, out var branchId))
            return "destinationLocation.branchId must be a POS branch ID (a number).";
        var destination = await _db.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.LocationId == branchId);
        if (destination is null)
            return $"destinationLocation.branchId {branchId} is not a POS branch.";
        // Stock ships from the Commissary to stores and bazaars, never into it.
        if (destination.LocationType == "Commissary")
            return $"destinationLocation.branchId {branchId} is the Commissary; transfers must go to a store or bazaar.";

        if (dto.Products is not { Count: > 0 })
            return "products must list at least one item.";

        for (var i = 0; i < dto.Products.Count; i++)
        {
            var line = dto.Products[i];
            if (string.IsNullOrWhiteSpace(line.Sku))
                return $"products[{i}].sku is required (it is how the item is matched to a POS product).";
            if (string.IsNullOrWhiteSpace(line.ProductName))
                return $"products[{i}].productName is required.";
            if (line.Quantity <= 0)
                return $"products[{i}].quantity must be greater than 0.";
        }

        return null;
    }
}
