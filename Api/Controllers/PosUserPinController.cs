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
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PosUserPinController(PosDbContext db, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
    }

    [HttpGet]
    public async Task<IActionResult> GetUserPin()
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser == null) return Unauthorized("User not identified.");

        var authUserId = currentUser.Id.ToString();
        if (string.IsNullOrEmpty(authUserId)) return Unauthorized("No auth user ID.");

        var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
        
        if (userPin == null)
        {
            var random = new Random();
            var pinCode = random.Next(100000, 999999).ToString();
            
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

        return Ok(new { pinCode = userPin.PinCode });
    }
}
