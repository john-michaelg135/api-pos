using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Infrastructures.Persistence;
using Domains.Entities;
using System.Security.Cryptography;
using Api.Middlewares;
using Applications.Interfaces;

namespace Api.Controllers;

public sealed record RevealPinRequest(string? Password);

[ApiController]
[Route("api-pos/user-pin")]
public class PosUserPinController : ControllerBase
{
    // Failed password checks allowed per manager before a short lockout.
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    private const int MaxPasswordLength = 256;

    private readonly PosDbContext _db;
    private readonly IAccountPasswordVerifier _passwordVerifier;
    private readonly IMemoryCache _cache;

    public PosUserPinController(PosDbContext db, IAccountPasswordVerifier passwordVerifier, IMemoryCache cache)
    {
        _db = db;
        _passwordVerifier = passwordVerifier;
        _cache = cache;
    }

    /// <summary>
    /// Reveals the signed-in manager's PIN only after their Portal account password is confirmed.
    /// The session identifies the manager; the body carries nothing but the password, so another
    /// manager's credentials can never unlock this PIN.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> RevealUserPin([FromBody] RevealPinRequest? request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (PosRoleAuthorization.RequireManager(HttpContext) is { } denied) return denied;

        var currentUser = HttpContext.GetCurrentUser();
        if (currentUser == null) return Unauthorized(new { message = "A valid POS session is required." });

        var authUserId = currentUser.PortalUserId;
        if (string.IsNullOrWhiteSpace(authUserId) && currentUser.Id != Guid.Empty)
        {
            authUserId = currentUser.Id.ToString();
        }
        if (string.IsNullOrWhiteSpace(authUserId)) return Unauthorized(new { message = "A valid POS session is required." });

        var password = request?.Password;
        if (string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
            return BadRequest(new { code = "password_required", message = "Enter your account password." });

        var attemptsKey = $"user-pin:failed:{authUserId}";
        if (_cache.TryGetValue(attemptsKey, out int failures) && failures >= MaxFailedAttempts)
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                code = "too_many_attempts",
                message = "Too many incorrect attempts. Wait a few minutes and try again.",
            });

        if (string.IsNullOrWhiteSpace(currentUser.Email))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "verification_unavailable",
                message = "Unable to verify your password. Please try again.",
            });

        var result = await _passwordVerifier.VerifyAsync(currentUser.Email, password, cancellationToken);
        if (result == PasswordCheckResult.Incorrect)
        {
            _cache.Set(attemptsKey, failures + 1, LockoutWindow);
            return UnprocessableEntity(new { code = "incorrect_password", message = "Incorrect password. Try again." });
        }
        if (result != PasswordCheckResult.Verified)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "verification_unavailable",
                message = "Unable to verify your password. Please try again.",
            });

        _cache.Remove(attemptsKey);

        var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId, cancellationToken);

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

            await _db.PosUserPins.AddAsync(userPin, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.IsNullOrEmpty(currentUser.Username) && currentUser.Username.Contains("demo", StringComparison.OrdinalIgnoreCase) && userPin.PinCode != "000000")
        {
            // If the DB already seeded a random PIN for a demo account prior to this patch, force it back to 000000
            userPin.PinCode = "000000";
            userPin.UpdatedAt = DateTime.UtcNow;
            _db.PosUserPins.Update(userPin);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { pinCode = userPin.PinCode });
    }
}
