using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Infrastructures.Persistence;
using Domains.Entities;
using System.Security.Cryptography;
using Api.Middlewares;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/user-pin")]
public class PosUserPinController : ControllerBase
{
    private readonly PosDbContext _db;

    public PosUserPinController(PosDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetUserPin()
    {
        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;

        var currentUser = HttpContext.GetCurrentUser();
        if (currentUser == null) return Unauthorized("User not identified.");

        var authUserId = currentUser.PortalUserId;
        if (string.IsNullOrWhiteSpace(authUserId) && currentUser.Id != Guid.Empty)
        {
            authUserId = currentUser.Id.ToString();
        }
        if (string.IsNullOrWhiteSpace(authUserId)) return Unauthorized("Portal user ID is missing.");

        var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
        
        if (userPin == null)
        {
            var pinCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

            // Override for demo accounts
            if (!string.IsNullOrEmpty(currentUser.Username) && currentUser.Username.Contains("demo", StringComparison.OrdinalIgnoreCase))
            {
                pinCode = "000000";
            }
            
            userPin = new PosUserPin
            {
                AuthUserId = authUserId,
                PinCode = pinCode,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            
            await _db.PosUserPins.AddAsync(userPin);
            await _db.SaveChangesAsync();
        }
        else if (!string.IsNullOrEmpty(currentUser.Username) && currentUser.Username.Contains("demo", StringComparison.OrdinalIgnoreCase) && userPin.PinCode != "000000")
        {
            // If the DB already seeded a random PIN for a demo account prior to this patch, force it back to 000000
            userPin.PinCode = "000000";
            userPin.UpdatedAt = DateTime.UtcNow;
            _db.PosUserPins.Update(userPin);
            await _db.SaveChangesAsync();
        }

        return Ok(new { pinCode = userPin.PinCode });
    }
}
