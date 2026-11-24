using Api.Contracts.Scms;
using Api.Contracts.StockRequests;
using Api.Middlewares;
using Applications.Audit;
using Applications.Interfaces;
using Domains.Entities;
using Infrastructures.Externals;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

/// <summary>The request, and whether this call created it (false when a clientRequestId replayed an earlier one).</summary>
public sealed record StockRequestCreateResult(StockRequestResponseDto Request, bool Created);

/// <summary>A stock request call that can't go ahead; <see cref="Status"/> is the HTTP status to answer with.</summary>
public sealed class StockRequestException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Branch stock requests to IMS (STOCK-REQUEST-INTEGRATION.md). POS saves the request, sends it to
/// IMS, and records what IMS decides; the delivery itself goes through the normal transfer manifest
/// and Receive Stock, which link and close the request.
/// </summary>
public class StockRequestService
{
    /// <summary>The first send plus the sweep's retries: about an hour at the 5-minute sweep.</summary>
    public const int MaxSendAttempts = 12;
    public const int MaxQuantity = 10000;
    public const int MaxNotesLength = 500;
    public const int MaxClientRequestIdLength = 64;
    public const int MaxListSize = 200;

    private readonly PosDbContext _db;
    private readonly ScmsApiClient _scms;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuditLogService _audit;
    private readonly ILogger<StockRequestService> _logger;

    public StockRequestService(PosDbContext db, ScmsApiClient scms, IHttpContextAccessor httpContextAccessor, IAuditLogService audit, ILogger<StockRequestService> logger)
    {
        _db = db;
        _scms = scms;
        _httpContextAccessor = httpContextAccessor;
        _audit = audit;
        _logger = logger;
    }

    // ── Branch: create and send ──

    public async Task<StockRequestCreateResult> CreateAsync(CreateStockRequestDto dto, CancellationToken cancellationToken = default)
    {
        var clientRequestId = string.IsNullOrWhiteSpace(dto.ClientRequestId) ? null : dto.ClientRequestId.Trim();
        if (clientRequestId is { Length: > MaxClientRequestIdLength })
            throw new StockRequestException(400, $"clientRequestId must be {MaxClientRequestIdLength} characters or fewer.");

        // A double submit of the same form: answer with the request the first one made.
        if (clientRequestId is not null && await FindReplayAsync(clientRequestId, dto, cancellationToken) is { } replay)
            return new(replay, Created: false);

        if (dto.Quantity is < 1 or > MaxQuantity)
            throw new StockRequestException(400, $"Quantity must be a whole number from 1 to {MaxQuantity}.");
        var notes = dto.Notes?.Trim() ?? string.Empty;
        if (notes.Length == 0)
            throw new StockRequestException(400, "Add a note so IMS knows why the stock is needed.");
        if (notes.Length > MaxNotesLength)
            throw new StockRequestException(400, $"Notes must be {MaxNotesLength} characters or fewer.");
        if (notes.IndexOfAny(['<', '>']) >= 0)
            throw new StockRequestException(400, "Notes cannot contain < or >.");

        var variation = await _db.ProductVariations
            .Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.VariationId == dto.VariationId, cancellationToken);
        if (variation is null || !variation.IsActive)
            throw new StockRequestException(400, "That item isn't an active product variation.");
        var sku = SkuOf(variation);
        if (sku is null)
            throw new StockRequestException(400, "This item has no SKU, so IMS can't match it. Ask an administrator to add one.");

        var location = await _db.Locations.FirstOrDefaultAsync(l => l.LocationId == dto.LocationId, cancellationToken);
        if (location is null || !location.IsActive)
            throw new StockRequestException(400, "That branch isn't an active POS location.");
        if (location.LocationType == "Commissary")
            throw new StockRequestException(400, "The Commissary ships stock; requests must come from a store or bazaar.");

        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (user is not null && !PosRoleAuthorization.IsAdministrator(user) && !user.LocationIds.Contains(location.LocationId))
            throw new StockRequestException(403, "You do not have access to this location.");

        // Several open requests for the same item are fine: IMS decides on each one (§1.1).

        var currentStock = await _db.Stocks
            .Where(s => s.VariationId == variation.VariationId && s.LocationId == location.LocationId)
            .Select(s => (int?)s.Quantity)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        var request = new StockRequest
        {
            // Placeholder until the identity is known; replaced with SR-000123 in the same transaction.
            RequestId = $"tmp-{Guid.NewGuid():N}",
            ClientRequestId = clientRequestId,
            VariationId = variation.VariationId,
            LocationId = location.LocationId,
            Sku = sku,
            Quantity = dto.Quantity,
            CurrentStockAtRequest = currentStock,
            Notes = notes,
            Status = StockRequestStatuses.Sending,
            RequestedBy = user?.PortalUserId ?? user?.Username ?? string.Empty,
            RequestedByName = user?.DisplayName ?? user?.Username ?? string.Empty,
            RequestedAt = DateTime.UtcNow,
            Variation = variation,
            Location = location,
        };

        await using (var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null)
        {
            try
            {
                _db.StockRequests.Add(request);
                await _db.SaveChangesAsync(cancellationToken);
                request.RequestId = FormatRequestId(request.Id);
                await _db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException) when (clientRequestId is not null)
            {
                // Two copies of the same submit raced past the check above; the unique key let one through.
                _db.ChangeTracker.Clear();
                if (await FindReplayAsync(clientRequestId, dto, cancellationToken) is { } raced)
                    return new(raced, Created: false);
                throw;
            }
        }

        await _audit.RecordAsync(StockAudit.StockRequested(request, StockAudit.ItemLabel(variation), location.LocationName));

        await SendAsync(request, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return new(Map(request), Created: true);
    }

    private async Task<StockRequestResponseDto?> FindReplayAsync(string clientRequestId, CreateStockRequestDto dto, CancellationToken cancellationToken)
    {
        var existing = await _db.StockRequests.AsNoTracking()
            .Include(r => r.Variation).ThenInclude(v => v!.Product)
            .Include(r => r.Location)
            .FirstOrDefaultAsync(r => r.ClientRequestId == clientRequestId, cancellationToken);
        if (existing is null) return null;
        if (existing.VariationId != dto.VariationId || existing.LocationId != dto.LocationId || existing.Quantity != dto.Quantity)
            throw new StockRequestException(409, "This form was already used for a different request. Reopen it and try again.");
        return Map(existing);
    }

    // ── Branch: cancel before IMS decides ──

    /// <summary>
    /// Withdraws a request IMS hasn't decided on (§3.6). IMS is asked first; the request becomes
    /// Cancelled only when IMS confirms (200) or never had it (404). Nothing changes otherwise.
    /// </summary>
    public async Task<StockRequestResponseDto> CancelAsync(string requestId, CancelStockRequestDto? dto, CancellationToken cancellationToken = default)
    {
        var reason = string.IsNullOrWhiteSpace(dto?.Reason) ? null : dto.Reason.Trim();
        if (reason is { Length: > MaxNotesLength })
            throw new StockRequestException(400, $"The reason must be {MaxNotesLength} characters or fewer.");
        if (reason is not null && reason.IndexOfAny(['<', '>']) >= 0)
            throw new StockRequestException(400, "The reason cannot contain < or >.");

        var request = await _db.StockRequests
            .Include(r => r.Variation).ThenInclude(v => v!.Product)
            .Include(r => r.Location)
            .FirstOrDefaultAsync(r => r.RequestId == requestId, cancellationToken);
        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        // Another branch's request is reported as not found, as Receive Stock does.
        if (request is null || (user is not null && !PosRoleAuthorization.IsAdministrator(user) && !user.LocationIds.Contains(request.LocationId)))
            throw new StockRequestException(404, $"Stock request {requestId} was not found.");

        if (request.Status == StockRequestStatuses.Cancelled) return Map(request);
        if (!StockRequestStatuses.Cancellable.Contains(request.Status))
            throw new StockRequestException(409, $"Stock request {requestId} is already {request.Status}, so it can't be cancelled.");

        var now = DateTime.UtcNow;
        var cancelledBy = user?.PortalUserId ?? user?.Username ?? string.Empty;
        var cancelledByName = user?.DisplayName ?? user?.Username ?? string.Empty;
        var result = await _scms.CancelStockRequestAsync(new StockRequestCancelDto
        {
            RequestId = request.RequestId,
            Reason = reason,
            CancelledBy = cancelledBy,
            CancelledByName = cancelledByName,
            CancelledAt = now,
        }, cancellationToken);

        switch (result.Outcome)
        {
            case StockRequestCancelOutcome.Refused:
                throw new StockRequestException(409, $"IMS couldn't cancel {requestId}: {result.Message}");
            case StockRequestCancelOutcome.Unreachable:
                _logger.LogWarning("Cancel of stock request {RequestId} didn't reach IMS: {Reason}", requestId, result.Message);
                throw new StockRequestException(503, "Couldn't reach IMS, so the request wasn't cancelled. Try again in a moment.");
        }

        // IMS's decision may have landed while we waited; never overwrite it.
        await _db.Entry(request).ReloadAsync(cancellationToken);
        if (!StockRequestStatuses.Cancellable.Contains(request.Status))
            throw new StockRequestException(409, $"Stock request {requestId} became {request.Status} while cancelling, so it wasn't cancelled.");

        var previousStatus = request.Status;
        request.Status = StockRequestStatuses.Cancelled;
        request.CancelledBy = cancelledBy;
        request.CancelledByName = cancelledByName;
        request.CancelledAt = now;
        request.CancelReason = reason;
        request.LastSendError = null;
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(StockAudit.StockRequestCancelled(
            request, StockAudit.ItemLabel(request.Variation), request.Location?.LocationName, previousStatus));

        return Map(request);
    }

    // ── Sweep: re-send what IMS hasn't acknowledged ──

    /// <summary>Re-sends requests still Sending (under the attempt cap). Returns how many IMS now has.</summary>
    public async Task<int> RetryPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _db.StockRequests
            .Include(r => r.Variation).ThenInclude(v => v!.Product)
            .Include(r => r.Location)
            .Where(r => r.Status == StockRequestStatuses.Sending && r.SendAttempts < MaxSendAttempts)
            .OrderBy(r => r.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        var delivered = 0;
        foreach (var request in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await SendAsync(request, cancellationToken);
            if (request.Status != StockRequestStatuses.Sending) delivered++;
        }

        if (pending.Count > 0) await _db.SaveChangesAsync(cancellationToken);
        return delivered;
    }

    private async Task SendAsync(StockRequest request, CancellationToken cancellationToken)
    {
        var variation = request.Variation;
        var location = request.Location;
        var body = new StockRequestDto
        {
            RequestId = request.RequestId,
            Branch = new DestinationBranchDto
            {
                BranchId = request.LocationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                BranchName = location?.LocationName ?? string.Empty,
            },
            Items =
            [
                new StockRequestItemDto
                {
                    Sku = request.Sku,
                    ProductId = string.IsNullOrWhiteSpace(variation?.Product?.ScmsProductId) ? null : variation.Product.ScmsProductId,
                    ProductName = variation?.Product?.ProductName ?? string.Empty,
                    VariationName = VariationLabel(variation),
                    Quantity = request.Quantity,
                    CurrentStock = request.CurrentStockAtRequest,
                },
            ],
            Notes = request.Notes,
            RequestedBy = request.RequestedBy,
            RequestedByName = request.RequestedByName,
            RequestedAt = request.RequestedAt,
        };

        var result = await _scms.SubmitStockRequestAsync(body, cancellationToken);
        request.SendAttempts++;

        // IMS may already have decided (its decision can land before our own 201 does).
        if (request.Status != StockRequestStatuses.Sending)
        {
            request.LastSendError = null;
            return;
        }

        switch (result.Outcome)
        {
            case StockRequestSubmitOutcome.Accepted:
                request.Status = StockRequestStatuses.Submitted;
                request.SentAt = DateTime.UtcNow;
                request.ImsRequestId = result.ImsRequestId ?? request.ImsRequestId;
                request.LastSendError = null;
                break;
            case StockRequestSubmitOutcome.Refused:
                // IMS looked at it and said no (unknown SKU, bad branch, …): final, with IMS's reason.
                request.Status = StockRequestStatuses.Rejected;
                request.RefusedOnSubmit = true;
                request.Remarks = result.Message;
                request.DecidedByName = "IMS";
                request.DecidedAt = DateTime.UtcNow;
                request.LastSendError = null;
                break;
            default:
                request.LastSendError = result.Message;
                if (request.SendAttempts >= MaxSendAttempts)
                    _logger.LogWarning("Stock request {RequestId} still hasn't reached IMS after {Attempts} attempts: {Reason}",
                        request.RequestId, request.SendAttempts, result.Message);
                break;
        }
    }

    // ── Branch: list ──

    public async Task<List<StockRequestResponseDto>> ListAsync(int? locationId, int? variationId, bool openOnly = false, CancellationToken cancellationToken = default)
    {
        var query = _db.StockRequests.AsNoTracking()
            .Include(r => r.Variation).ThenInclude(v => v!.Product)
            .Include(r => r.Location)
            .AsQueryable();

        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (user is not null && !PosRoleAuthorization.IsAdministrator(user))
        {
            var allowed = user.LocationIds;
            query = query.Where(r => allowed.Contains(r.LocationId));
        }
        if (locationId.HasValue) query = query.Where(r => r.LocationId == locationId.Value);
        if (variationId.HasValue) query = query.Where(r => r.VariationId == variationId.Value);
        if (openOnly) query = query.Where(r => StockRequestStatuses.Open.Contains(r.Status));

        var requests = await query.OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id).Take(MaxListSize).ToListAsync(cancellationToken);
        return requests.Select(Map).ToList();
    }

    // ── IMS: approve or reject ──

    public async Task<StockRequestResponseDto> ApplyDecisionAsync(string requestId, StockRequestDecisionDto dto, CancellationToken cancellationToken = default)
    {
        var status = dto.Status?.Trim();
        if (status is not (StockRequestStatuses.Approved or StockRequestStatuses.Rejected))
            throw new StockRequestException(400, "status must be \"Approved\" or \"Rejected\".");

        var remarks = string.IsNullOrWhiteSpace(dto.Remarks) ? null : dto.Remarks.Trim();
        if (remarks is { Length: > MaxNotesLength })
            throw new StockRequestException(400, $"remarks must be {MaxNotesLength} characters or fewer.");
        if (remarks is not null && remarks.IndexOfAny(['<', '>']) >= 0)
            throw new StockRequestException(400, "remarks cannot contain < or >.");
        if (status == StockRequestStatuses.Rejected && remarks is null)
            throw new StockRequestException(400, "remarks are required when rejecting, so the branch knows why.");
        if (string.IsNullOrWhiteSpace(dto.DecidedBy) || string.IsNullOrWhiteSpace(dto.DecidedByName))
            throw new StockRequestException(400, "decidedBy and decidedByName are required.");

        var request = await _db.StockRequests
            .Include(r => r.Variation).ThenInclude(v => v!.Product)
            .Include(r => r.Location)
            .FirstOrDefaultAsync(r => r.RequestId == requestId, cancellationToken)
            ?? throw new StockRequestException(404, $"Stock request {requestId} was not found.");

        if (status == StockRequestStatuses.Approved &&
            (dto.ApprovedQuantity is not { } approved || approved < 1 || approved > request.Quantity))
            throw new StockRequestException(400, $"approvedQuantity must be from 1 to the {request.Quantity} requested.");

        // The same decision sent again (an IMS retry) changes nothing, even after the delivery moved it on.
        var sameDecision = status == StockRequestStatuses.Approved
            ? request.Status is StockRequestStatuses.Approved or StockRequestStatuses.InTransit or StockRequestStatuses.Fulfilled
              && request.ApprovedQuantity == dto.ApprovedQuantity
            : request.Status == StockRequestStatuses.Rejected && request.DecidedBy == dto.DecidedBy.Trim();
        if (sameDecision) return Map(request);

        // Sending counts as undecided: IMS can have the request before POS saw its 201.
        if (request.Status is not (StockRequestStatuses.Submitted or StockRequestStatuses.Sending))
            throw new StockRequestException(409, $"Stock request {requestId} is already {request.Status}; it can't be {status.ToLowerInvariant()} now.");

        var previousStatus = request.Status;
        request.Status = status;
        request.ApprovedQuantity = status == StockRequestStatuses.Approved ? dto.ApprovedQuantity : null;
        request.Remarks = remarks;
        if (!string.IsNullOrWhiteSpace(dto.ImsRequestId)) request.ImsRequestId = dto.ImsRequestId.Trim();
        request.DecidedBy = dto.DecidedBy.Trim();
        request.DecidedByName = dto.DecidedByName.Trim();
        request.DecidedAt = dto.DecidedAt == default ? DateTime.UtcNow : dto.DecidedAt.ToUniversalTime();
        request.SentAt ??= DateTime.UtcNow;
        request.LastSendError = null;
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(StockAudit.StockRequestDecided(
            request, StockAudit.ItemLabel(request.Variation), request.Location?.LocationName, previousStatus));

        return Map(request);
    }

    // ── Manifest link (called by TransfersController before the transfer is saved) ──

    /// <summary>
    /// Checks a manifest's stockRequestId (§3.3). Returns the problem to answer 400 with, or the
    /// tracked request to link. A manifest without stockRequestId returns (null, null).
    /// </summary>
    public async Task<(string? Problem, StockRequest? Request)> CheckManifestAsync(TransferDetailsDto dto, CancellationToken cancellationToken = default)
    {
        var requestId = dto.StockRequestId?.Trim();
        if (string.IsNullOrEmpty(requestId)) return (null, null);
        if (requestId.Length > 50) return ("stockRequestId must be 50 characters or fewer.", null);

        var request = await _db.StockRequests.FirstOrDefaultAsync(r => r.RequestId == requestId, cancellationToken);
        if (request is null)
            return ($"stockRequestId {requestId} is not a POS stock request.", null);
        if (request.Status != StockRequestStatuses.Approved)
            return ($"Stock request {requestId} is {request.Status}; only an Approved request can be shipped (one delivery per request).", null);
        if (dto.DestinationLocation?.BranchId?.Trim() != request.LocationId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return ($"Stock request {requestId} is for branch {request.LocationId}, not {dto.DestinationLocation?.BranchId}.", null);
        if (dto.Products?.Any(p => string.Equals(p.Sku?.Trim(), request.Sku, StringComparison.OrdinalIgnoreCase)) != true)
            return ($"Stock request {requestId} is for SKU {request.Sku}, which this manifest doesn't include.", null);

        return (null, request);
    }

    /// <summary>Marks a checked request In Transit on the given (unsaved) transfer; the caller saves both.</summary>
    public static void LinkTransfer(StockRequest request, StockTransfer transfer)
    {
        transfer.StockRequestId = request.RequestId;
        request.TransferId = transfer.TransferId;
        request.ShippedAt = DateTime.UtcNow;
        request.Status = StockRequestStatuses.InTransit;
    }

    // ── Helpers ──

    /// <summary>The SKU IMS knows the variation by: the Sku column, or the "SKU|…" name prefix of older rows.</summary>
    public static string? SkuOf(ProductVariation variation)
    {
        var sku = !string.IsNullOrWhiteSpace(variation.Sku) ? variation.Sku.Trim() : variation.VariationName.Split('|')[0].Trim();
        // An unprefixed name ("Pouch 250g") is not a SKU.
        if (string.IsNullOrWhiteSpace(variation.Sku) && !variation.VariationName.Contains('|')) return null;
        return sku.Length == 0 ? null : sku;
    }

    public static string FormatRequestId(int id) => $"SR-{id:D6}";

    private static string VariationLabel(ProductVariation? variation)
    {
        var name = variation?.VariationName ?? string.Empty;
        if (!name.Contains('|')) return name;
        return string.Join(" ", name.Split('|').Skip(1).Select(p => p.Trim()).Where(p => p.Length > 0));
    }

    private static StockRequestResponseDto Map(StockRequest r) => new()
    {
        RequestId = r.RequestId,
        VariationId = r.VariationId,
        VariationName = VariationLabel(r.Variation),
        ProductName = r.Variation?.Product?.ProductName ?? string.Empty,
        Sku = r.Sku,
        LocationId = r.LocationId,
        LocationName = r.Location?.LocationName ?? string.Empty,
        Quantity = r.Quantity,
        ApprovedQuantity = r.ApprovedQuantity,
        ReceivedQuantity = r.ReceivedQuantity,
        Notes = r.Notes,
        Status = r.Status,
        CanCancel = StockRequestStatuses.Cancellable.Contains(r.Status),
        Remarks = r.Remarks,
        RefusedOnSubmit = r.RefusedOnSubmit,
        LastSendError = r.Status == StockRequestStatuses.Sending ? r.LastSendError : null,
        RequestedByName = r.RequestedByName,
        RequestedAt = r.RequestedAt,
        SentAt = r.SentAt,
        DecidedByName = r.DecidedByName,
        DecidedAt = r.DecidedAt,
        TransferId = r.TransferId,
        ShippedAt = r.ShippedAt,
        FulfilledAt = r.FulfilledAt,
        CancelledByName = r.CancelledByName,
        CancelledAt = r.CancelledAt,
        CancelReason = r.CancelReason,
    };
}
