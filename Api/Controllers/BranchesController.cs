using Microsoft.AspNetCore.Mvc;
using Api.Contracts.Scms;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/branches")]
public class BranchesController : ControllerBase
{
    private readonly PosDbContext _db;

    public BranchesController(PosDbContext db)
    {
        _db = db;
    }

    // GET api-pos/branches
    [HttpGet]
    [ProducesResponseType(typeof(BranchListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllBranches()
    {
        var locations = await _db.Locations
            .AsNoTracking()
            .Where(l => (l.LocationType == "Store" || l.LocationType == "Bazaar") && l.IsActive)
            .ToListAsync();

        var response = new BranchListResponseDto
        {
            Items = locations.Select(l => new BranchItemDto
            {
                BranchId = l.LocationId.ToString(), // Simple int to string
                BranchName = l.LocationName,
                Address = "Not Available", // Update based on location table fields
                Status = "Active"
            }).ToList()
        };

        return Ok(response);
    }

    // GET api-pos/branches/{branchId}/inventory
    [HttpGet("{branchId}/inventory")]
    [ProducesResponseType(typeof(BranchInventoryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBranchInventory(string branchId)
    {
        if (!int.TryParse(branchId, out var locId))
        {
            return BadRequest("Invalid branchId format. Must be integer.");
        }

        var location = await _db.Locations.FindAsync(locId);
        if (location == null) return NotFound("Branch not found.");

        var stocks = await _db.Stocks
            .AsNoTracking()
            .Where(s => s.LocationId == locId)
            .Include(s => s.Variation)
                .ThenInclude(v => v.Product)
            .ToListAsync();

        var response = new BranchInventoryDto
        {
            BranchId = branchId,
            BranchName = location.LocationName,
            Items = stocks.Select(s => new BranchInventoryItemDto
            {
                // In POS, variation corresponds to what is stocked, but SCMS deals in Products.
                // Assuming SCMS maps to variation or Product:
                ProductId = s.Variation?.ProductId.ToString() ?? "N/A",
                ProductName = s.Variation?.Product?.ProductName ?? "Unknown",
                AvailableStocks = s.Quantity,
                MinimumStockLevel = s.MinThreshold,
                StockStatus = s.Quantity <= s.MinThreshold ? "Low Stock" : "Normal"
            }).ToList()
        };

        return Ok(response);
    }
}
