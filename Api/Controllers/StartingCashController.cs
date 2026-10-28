using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Infrastructures.Persistence;
using Domains.Entities;
using Api.Middlewares;
using Applications.Interfaces;
using Applications.Services;
using Npgsql;
using NpgsqlTypes;

namespace Api.Controllers;

public class SetStartingCashDto
{
    public int? LocationId { get; set; }
    public decimal Amount { get; set; }
    public string? CashierName { get; set; }
}

public class EndShiftCashDto
{
    public int? LocationId { get; set; }
    public decimal? Amount { get; set; }
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
    public decimal? EndShiftAmount { get; set; }
    public DateTime? EndShiftAt { get; set; }
    public string? EndShiftCashierName { get; set; }
    public decimal? SuccessfulSalesAmount { get; set; }
    public decimal? ApprovedRefundAmount { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? CashVariance { get; set; }
    public decimal? CashShortage { get; set; }
    /// <summary>Closed by the system at the end of the business day, not by a cashier.</summary>
    public bool AutoClosed { get; set; }
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
    private readonly IShiftAutoCloseService _shiftAutoClose;
    private readonly bool _authEnabled;

    public StartingCashController(
        PosDbContext db,
        IConfiguration configuration,
        IPortalUserDirectory portalUserDirectory,
        IShiftAutoCloseService shiftAutoClose)
    {
        _db = db;
        _portalUserDirectory = portalUserDirectory;
        _shiftAutoClose = shiftAutoClose;
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

    private static StartingCashDto Map(
        StartingCash s,
        string? cashierName = null,
        string? endShiftCashierName = null) => new()
    {
        Id = s.Id,
        CashierName = cashierName ?? s.CashierName,
        LocationId = s.LocationId,
        LocationName = s.Location?.LocationName,
        BusinessDate = s.BusinessDate,
        Amount = s.Amount,
        CreatedAt = s.CreatedAt,
        EndShiftAmount = s.EndShiftAmount,
        EndShiftAt = s.EndShiftAt,
        EndShiftCashierName = endShiftCashierName ?? s.EndShiftCashierName,
        SuccessfulSalesAmount = s.SuccessfulSalesAmount,
        ApprovedRefundAmount = s.ApprovedRefundAmount,
        ExpectedCash = s.ExpectedCash,
        CashVariance = s.EndShiftAmount.HasValue && s.ExpectedCash.HasValue
            ? s.EndShiftAmount.Value - s.ExpectedCash.Value
            : null,
        CashShortage = s.EndShiftAmount.HasValue && s.ExpectedCash.HasValue
            ? Math.Max(s.ExpectedCash.Value - s.EndShiftAmount.Value, 0)
            : null,
        AutoClosed = s.AutoClosed,
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

    [HttpPost("end-shift")]
    [RateLimit(WindowSeconds = 5, MaxRequests = 1, Message = "End shift is already being submitted. Please wait a moment.")]
    public async Task<IActionResult> EndShift(
        [FromBody] EndShiftCashDto dto,
        CancellationToken cancellationToken)
    {
        if (!dto.Amount.HasValue) return BadRequest("End-of-shift cash is required.");
        if (dto.Amount.Value < 0) return BadRequest("End-of-shift cash cannot be negative.");
        if (dto.Amount.Value > MaxAmount) return BadRequest("End-of-shift cash is unrealistically large. Please check the amount.");

        var user = HttpContext.GetCurrentUser();
        if (user is null) return Unauthorized(new { message = "A valid POS session is required." });
        if (!string.Equals(user.Role, "Cashier", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(user.SubRole, "Cashier", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Only Cashiers can submit an end-of-shift cash count." });
        }

        var (authUserId, name, _) = Identity();
        if (authUserId == "anonymous")
            return Unauthorized(new { message = "A valid POS user is required to submit an end-of-shift cash count." });

        var locationId = ResolveLocation(user, dto.LocationId);
        if (PosRoleAuthorization.RequireLocationAccess(HttpContext, locationId) is { } denied) return denied;
        if (!locationId.HasValue) return BadRequest("A branch is required to record end-of-shift cash.");

        var businessDate = Today();
        var entry = await _db.StartingCashEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(
                record => record.LocationId == locationId.Value && record.BusinessDate == businessDate,
                cancellationToken);
        if (entry is null)
            return BadRequest("Starting cash must be recorded before ending the shift.");
        if (entry.EndShiftAt.HasValue)
            return Conflict(new { message = "End-of-shift cash has already been recorded for this branch today." });

        var recordedAt = DateTime.UtcNow;
        var (sales, refunds) = await CalculateShiftTotalsAsync(entry, recordedAt, cancellationToken);

        var expectedCash = entry.Amount + sales - refunds;
        var updated = await _db.StartingCashEntries
            .Where(record => record.Id == entry.Id && record.EndShiftAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(record => record.EndShiftAmount, Math.Round(dto.Amount.Value, 2))
                .SetProperty(record => record.EndShiftAt, recordedAt)
                .SetProperty(record => record.EndShiftAuthUserId, authUserId)
                .SetProperty(record => record.EndShiftCashierName, name)
                .SetProperty(record => record.SuccessfulSalesAmount, sales)
                .SetProperty(record => record.ApprovedRefundAmount, refunds)
                .SetProperty(record => record.ExpectedCash, expectedCash),
                cancellationToken);
        if (updated == 0)
            return Conflict(new { message = "End-of-shift cash has already been recorded for this branch today." });

        var closedEntry = await _db.StartingCashEntries
            .AsNoTracking()
            .Include(record => record.Location)
            .FirstAsync(record => record.Id == entry.Id, cancellationToken);
        return Ok(Map(closedEntry));
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

        // Close any past shift nobody ended, even if the background sweep hasn't run (e.g. the host slept).
        await _shiftAutoClose.CloseOverdueShiftsAsync(cancellationToken);

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

        var closedEntries = entries.Where(entry => entry.EndShiftAt.HasValue).ToArray();
        var reconciledTotals = await RecalculateClosedEntriesAsync(closedEntries, cancellationToken);
        foreach (var entry in closedEntries)
        {
            if (!reconciledTotals.TryGetValue(entry.Id, out var totals)) continue;
            entry.SuccessfulSalesAmount = totals.Sales;
            entry.ApprovedRefundAmount = totals.Refunds;
            entry.ExpectedCash = entry.Amount + totals.Sales - totals.Refunds;
        }

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
            var endShiftUser = entry.EndShiftAuthUserId is { Length: > 0 } endShiftUserId
                ? usersById.GetValueOrDefault(endShiftUserId)
                : null;
            var displayName = portalUser is null
                ? null
                : $"{portalUser.FirstName} {portalUser.LastName}".Trim();
            var endShiftDisplayName = endShiftUser is null
                ? null
                : $"{endShiftUser.FirstName} {endShiftUser.LastName}".Trim();
            return Map(entry,
                string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                string.IsNullOrWhiteSpace(endShiftDisplayName) ? null : endShiftDisplayName);
        }).ToList());
    }

    private async Task<(decimal Sales, decimal Refunds)> CalculateShiftTotalsAsync(
        StartingCash entry,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        var utcStart = entry.BusinessDate
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            .AddHours(-8);

        var successfulPayments = await _db.Payments
            .AsNoTracking()
            .Where(payment =>
                (payment.PaymentStatus == "Success" || payment.PaymentStatus == "Paid")
                && payment.PaidAt >= utcStart
                && payment.PaidAt < cutoff
                && payment.Order != null
                && payment.Order.LocationId == entry.LocationId
                && payment.Order.OrderStatus != "Voided"
                && payment.Order.OrderStatus != "Cancelled")
            .SumAsync(payment => (decimal?)payment.AmountPaid, cancellationToken) ?? 0m;

        var paidOrdersWithoutPayments = await _db.Orders
            .AsNoTracking()
            .Where(order =>
                order.LocationId == entry.LocationId
                && order.CreatedAt >= utcStart
                && order.CreatedAt < cutoff
                && order.PaymentStatus == "Paid"
                && order.OrderStatus != "Voided"
                && order.OrderStatus != "Cancelled"
                && !_db.Payments.Any(payment =>
                    payment.OrderId == order.OrderId
                    && (payment.PaymentStatus == "Success" || payment.PaymentStatus == "Paid")))
            .SumAsync(order => (decimal?)order.TotalAmount, cancellationToken) ?? 0m;

        var approvedRefundRequests = await (
            from refund in _db.RefundRequests.AsNoTracking()
            join item in _db.OrderItems.AsNoTracking()
                on new { refund.OrderId, refund.VariationId }
                equals new { item.OrderId, item.VariationId }
            where refund.LocationId == entry.LocationId
                && refund.Status == "Approved"
                && refund.ApprovedAt.HasValue
                && refund.ApprovedAt.Value >= utcStart
                && refund.ApprovedAt.Value < cutoff
            select item.UnitPrice * refund.QuantityToReturn
        ).SumAsync(cancellationToken);

        var approvedWholeOrderRefunds = await _db.Orders
            .AsNoTracking()
            .Where(order =>
                order.LocationId == entry.LocationId
                && order.OrderStatus == "Refunded"
                && order.ApprovedAt.HasValue
                && order.ApprovedAt.Value >= utcStart
                && order.ApprovedAt.Value < cutoff
                && !_db.RefundRequests.Any(refund =>
                    refund.OrderId == order.OrderId
                    && refund.Status == "Approved"))
            .SumAsync(order => (decimal?)order.TotalAmount, cancellationToken) ?? 0m;

        return (
            successfulPayments + paidOrdersWithoutPayments,
            approvedRefundRequests + approvedWholeOrderRefunds);
    }

    private async Task<Dictionary<int, (decimal Sales, decimal Refunds)>> RecalculateClosedEntriesAsync(
        IReadOnlyCollection<StartingCash> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return new Dictionary<int, (decimal Sales, decimal Refunds)>();

        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var closeConnectionWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeConnectionWhenDone)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    e."Id",
                    COALESCE((
                        SELECT SUM(p."AmountPaid")
                        FROM public."Payments" p
                        JOIN public."Orders" o ON o."OrderId" = p."OrderId"
                        WHERE o."LocationId" = e."LocationId"
                          AND p."PaymentStatus" IN ('Success', 'Paid')
                          AND p."PaidAt" >= (e."BusinessDate"::timestamp AT TIME ZONE 'Asia/Manila')
                          AND p."PaidAt" < e."EndShiftAt"
                          AND o."OrderStatus" NOT IN ('Voided', 'Cancelled')
                    ), 0)
                    +
                    COALESCE((
                        SELECT SUM(o."TotalAmount")
                        FROM public."Orders" o
                        WHERE o."LocationId" = e."LocationId"
                          AND o."CreatedAt" >= (e."BusinessDate"::timestamp AT TIME ZONE 'Asia/Manila')
                          AND o."CreatedAt" < e."EndShiftAt"
                          AND o."PaymentStatus" = 'Paid'
                          AND o."OrderStatus" NOT IN ('Voided', 'Cancelled')
                          AND NOT EXISTS (
                              SELECT 1
                              FROM public."Payments" p
                              WHERE p."OrderId" = o."OrderId"
                                AND p."PaymentStatus" IN ('Success', 'Paid')
                          )
                    ), 0) AS "Sales",
                    COALESCE((
                        SELECT SUM(item."UnitPrice" * refund."QuantityToReturn")
                        FROM public."RefundRequests" refund
                        JOIN public."OrderItems" item
                          ON item."OrderId" = refund."OrderId"
                         AND item."VariationId" = refund."VariationId"
                        WHERE refund."LocationId" = e."LocationId"
                          AND refund."Status" = 'Approved'
                          AND refund."ApprovedAt" >= (e."BusinessDate"::timestamp AT TIME ZONE 'Asia/Manila')
                          AND refund."ApprovedAt" < e."EndShiftAt"
                    ), 0)
                    +
                    COALESCE((
                        SELECT SUM(o."TotalAmount")
                        FROM public."Orders" o
                        WHERE o."LocationId" = e."LocationId"
                          AND o."OrderStatus" = 'Refunded'
                          AND o."ApprovedAt" >= (e."BusinessDate"::timestamp AT TIME ZONE 'Asia/Manila')
                          AND o."ApprovedAt" < e."EndShiftAt"
                          AND NOT EXISTS (
                              SELECT 1
                              FROM public."RefundRequests" refund
                              WHERE refund."OrderId" = o."OrderId"
                                AND refund."Status" = 'Approved'
                          )
                    ), 0) AS "Refunds"
                FROM public."StartingCashEntries" e
                WHERE e."Id" = ANY(@entryIds)
                  AND e."EndShiftAt" IS NOT NULL
                """;
            command.Parameters.AddWithValue(
                "entryIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer,
                entries.Select(entry => entry.Id).ToArray());

            var totals = new Dictionary<int, (decimal Sales, decimal Refunds)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                totals[reader.GetInt32(0)] = (reader.GetDecimal(1), reader.GetDecimal(2));
            }

            return totals;
        }
        finally
        {
            if (closeConnectionWhenDone)
                await connection.CloseAsync();
        }
    }
}
