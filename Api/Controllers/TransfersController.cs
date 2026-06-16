using Microsoft.AspNetCore.Mvc;
using Api.Contracts.Scms;
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
            TransferDate = dto.TransferDate,
            Status = dto.Status, // E.g. "Pending Receiving"
            Items = dto.Products.Select(p => new StockTransferItem
            {
                ProductId = p.ProductId,
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

    // POST api-pos/transfers/{transferId}/receive
    [HttpPost("{transferId}/receive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReceiveTransfer(string transferId, [FromServices] Applications.Interfaces.IScmsIntegrationService scmsService, [FromServices] Applications.Interfaces.IInventoryService inventoryService, [FromBody] ReceiveConfirmationDto dto)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.TransferId == transferId);

        if (transfer == null) return NotFound("Transfer not found.");
        if (transfer.Status == "Completed") return BadRequest("Transfer is already completed.");

        // Convert the string BranchId back to int LocationId for inventory receiving
        if (!int.TryParse(transfer.DestinationBranchId, out var locationId))
        {
            return BadRequest("Invalid BranchId for POS inventory.");
        }

        foreach (var prod in dto.ReceivedProducts)
        {
            // Find variation id mapping. For now, assuming ProductId = VariationId or we just use it directly.
            // In a real system, you'd map ProductId to VariationId.
            if (!int.TryParse(prod.ProductId, out var varId)) continue;

            await inventoryService.ReceiveStockAsync(new Api.Contracts.Inventory.StockReceivingDto
            {
                VariationId = varId,
                LocationId = locationId,
                QuantityReceived = prod.ReceivedQuantity,
                Notes = dto.Notes,
                ReceivedBy = null // You can map from context
            });
        }

        transfer.Status = "Completed";
        _db.StockTransfers.Update(transfer);
        await _db.SaveChangesAsync();

        // Notify SCMS
        await scmsService.ConfirmTransferAsync(transferId, dto);

        return Ok(new { success = true, message = "Transfer received successfully." });
    }
}
