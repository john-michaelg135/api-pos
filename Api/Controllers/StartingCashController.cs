using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Infrastructures.Persistence;
using Domains.Entities;
using Api.Middlewares;
using Applications.Interfaces;
using Applications.Services;

namespace Api.Controllers;

public class SetStartingCashDto
{
    public int? LocationId { get; set; }
    public decimal Amount { get; set; }
    public string? CashierName { get; set; }
}

public class StartingCashDto
{
    public int Id { get; set; }
    public string CashierName { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string? LocationName { get; set; }
    public DateOnly BusinessDate { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Cashier declares the starting cash in the drawer before selling; managers
/// can review every declaration.
/// </summary>
[ApiController]
[Route("api-pos/starting-cash")]
public class StartingCashController : ControllerBase
{
    private const decimal MaxAmount = 10_000_000m;

    private readonly PosDbContext _db;
    private readonly IPortalUserDirectory _portalUserDirectory;
    private readonly bool _authEnabled;

    public StartingCashController(
        PosDbContext db,
        IConfiguration configuration,
        IPortalUserDirectory portalUserDirectory)
    {
        _db = db;
        _portalUserDirectory = portalUserDirectory;
        _authEnabled = string.Equals(
            configuration["AUTH_MIDDLEWARE_ENABLED"], "true",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Business date in Philippine time (UTC+8).</summary>
    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    private (string AuthUserId, string Name, CurrentUserContext? User) Identity(string? fallbackName = null)
    {
        var user = HttpContext.GetCurrentUser();
        var id = user?.PortalUserId
            ?? (user?.Id != Guid.Empty ? user?.Id.ToString() : null)
            ?? "anonymous";

        // 1. Prefer authenticated username if valid and not "Unknown"
        var rawName = !string.IsNullOrWhiteSpace(user?.DisplayName)
            ? user.DisplayName
            : !string.IsNullOrWhiteSpace(user?.Username) && !string.Equals(user.Username, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? user.Username
                : fallbackName;

        // 2. If authenticated username is an email address but the UI sent a human display name:
        if (!string.IsNullOrWhiteSpace(fallbackName) && (string.IsNullOrWhiteSpace(rawName) || rawName.Contains('@')))
        {
            rawName = fallbackName;
        }

        var name = !string.IsNullOrWhiteSpace(rawName) ? rawName.Trim() : "Unknown";
        return (id, name, user);
    }

    /// <summary>Cashiers are pinned to their branch; others use the supplied one.</summary>
    private static int? ResolveLocation(CurrentUserContext? user, int? requested)
    {
        if (user is { IsSuperUser: false })
        {
            if (requested.HasValue && user.LocationIds.Contains(requested.Value)) return requested;
            return user.LocationId;
        }

        return requested ?? user?.LocationId;
    }

    /// <summary>
    /// Manager-only. A resolved non-manager is always denied (even in demo mode);
    /// an unresolved user is only tolerated when auth enforcement is off (local dev).
    /// </summary>
    private IActionResult? RequireManager()
    {
        var user = HttpContext.GetCurrentUser();
        if (user == null)
        {
            return _authEnabled
                ? Unauthorized(new { message = "Unauthorized: unable to resolve the current user." })
                : null;
        }

        var isManager = PosRoleAuthorization.IsManager(user)
            || user.Can(PosModules.OrderManagement, PosAction.Approve)
            || string.Equals(user.SubRole, "Manager", StringComparison.OrdinalIgnoreCase);

        return isManager
            ? null
            : StatusCode(StatusCodes.Status403Forbidden, new { message = "Forbidden: only managers can view starting cash records." });
    }

    private static StartingCashDto Map(StartingCash s, string? cashierName = null) => new()
    {
        Id = s.Id,
        CashierName = cashierName ?? s.CashierName,
        LocationId = s.LocationId,
        LocationName = s.Location?.LocationName,
        BusinessDate = s.BusinessDate,
        Amount = s.Amount,
        CreatedAt = s.CreatedAt
    };

    // GET api-pos/starting-cash/today?locationId=1&cashierName=Jane → { entry: {...} | null }
    [HttpGet("today")]
    public async Task<IActionResult> GetToday([FromQuery] int? locationId, [FromQuery] string? cashierName)
    {
        var (authUserId, name, user) = Identity(cashierName);
        var loc = ResolveLocation(user, locationId);
        if (PosRoleAuthorization.RequireLocationAccess(HttpContext, loc) is { } denied) return denied;
        if (!loc.HasValue) return Ok(new { entry = (StartingCashDto?)null });

        var today = Today();

        // Location-based validation: Find today's starting cash declaration for this branch location
        var entry = await _db.StartingCashEntries
            .Include(s => s.Location)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(s => s.LocationId == loc.Value && s.BusinessDate == today);

        // Auto-heal legacy or previously recorded "Unknown" cashier names when the active cashier is resolved
        if (entry != null && entry.CashierName == "Unknown" && name != "Unknown")
        {
            entry.CashierName = name;
            if (authUserId != "anonymous" && entry.AuthUserId == "anonymous")
            {
                entry.AuthUserId = authUserId;
            }
            await _db.SaveChangesAsync();
        }

        return Ok(new { entry = entry == null ? null : Map(entry) });
    }

    // POST api-pos/starting-cash
    [HttpPost]
    [RateLimit(WindowSeconds = 5, MaxRequests = 1, Message = "Starting cash is already being submitted. Please wait a moment.")]
    public async Task<IActionResult> Set([FromBody] SetStartingCashDto dto)
    {
        if (dto == null) return BadRequest("Starting cash is required.");
        if (dto.Amount < 0) return BadRequest("Starting cash cannot be negative.");
        if (dto.Amount > MaxAmount) return BadRequest("Starting cash is unrealistically large. Please check the amount.");

        var (authUserId, name, user) = Identity(dto.CashierName);
        var loc = ResolveLocation(user, dto.LocationId);
        if (PosRoleAuthorization.RequireLocationAccess(HttpContext, loc) is { } denied) return denied;
        if (!loc.HasValue) return BadRequest("A branch is required to record starting cash.");

        var location = await _db.Locations.FirstOrDefaultAsync(l => l.LocationId == loc.Value);
        if (location == null) return BadRequest($"Location with ID {loc.Value} not found.");

        var today = Today();

        // Proper location validation: Check if this branch location already has starting cash recorded for today
        var existing = await _db.StartingCashEntries
            .Include(s => s.Location)
            .FirstOrDefaultAsync(s => s.LocationId == loc.Value && s.BusinessDate == today);

        if (existing != null)
        {
            // If the record exists and had "Unknown" name, heal it with the real name
            if (existing.CashierName == "Unknown" && name != "Unknown")
            {
                existing.CashierName = name;
                if (authUserId != "anonymous" && existing.AuthUserId == "anonymous")
                {
                    existing.AuthUserId = authUserId;
                }
                await _db.SaveChangesAsync();
                return Ok(Map(existing));
            }
            return BadRequest($"Starting cash for {location.LocationName} was already recorded for today.");
        }

        var entry = new StartingCash
        {
            AuthUserId = authUserId,
            CashierName = name,
            LocationId = loc.Value,
            BusinessDate = today,
            Amount = Math.Round(dto.Amount, 2),
            CreatedAt = DateTime.UtcNow,
            Location = location
        };

        _db.StartingCashEntries.Add(entry);
        await _db.SaveChangesAsync();

        return Ok(Map(entry));
    }

    // GET api-pos/starting-cash?locationId=&from=2026-10-01&to=2026-10-09   (manager only)
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? locationId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (RequireManager() is { } denied) return denied;

        // Branch managers are strictly limited to their own branch; super users see all with filtering.
        var user = HttpContext.GetCurrentUser();
        var isSuperUser = user?.IsSuperUser == true;

        var query = _db.StartingCashEntries.AsNoTracking().Include(s => s.Location).AsQueryable();

        if (!isSuperUser)
        {
            var assignedLocationIds = user?.LocationIds ?? Array.Empty<int>();
            if (locationId.HasValue && !assignedLocationIds.Contains(locationId.Value))
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "You do not have access to this location." });

            query = query.Where(entry => assignedLocationIds.Contains(entry.LocationId));
        }
        else if (locationId.HasValue)
        {
            query = query.Where(s => s.LocationId == locationId.Value);
        }

        if (from.HasValue) query = query.Where(s => s.BusinessDate >= from.Value);
        if (to.HasValue) query = query.Where(s => s.BusinessDate <= to.Value);

        var entries = await query
            .OrderByDescending(s => s.CreatedAt)
            .Take(500)
            .ToListAsync();

        IReadOnlyList<PortalPosUser> portalUsers;
        try
        {
            portalUsers = await _portalUserDirectory.ListEligiblePosUsersAsync(cancellationToken);
        }
        catch (PortalDirectoryNotConfiguredException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "The read-only Enterprise Portal staff directory is not configured for api-pos.",
            });
        }

        var usersById = portalUsers.ToDictionary(user => user.Id, StringComparer.Ordinal);
        var usersByUsername = portalUsers.ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);

        return Ok(entries.Select(entry =>
        {
            var portalUser = usersById.GetValueOrDefault(entry.AuthUserId)
                ?? usersByUsername.GetValueOrDefault(entry.CashierName);
            var displayName = portalUser is null
                ? null
                : $"{portalUser.FirstName} {portalUser.LastName}".Trim();
            return Map(entry, string.IsNullOrWhiteSpace(displayName) ? null : displayName);
        }).ToList());
    }
}
