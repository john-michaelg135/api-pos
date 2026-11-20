using Applications.Interfaces;
using Api.Contracts.OrderManagement;
using Api.Contracts.OrderEntry;
using Api.Contracts.Shared;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Api.Middlewares;
using Applications.Audit;
using Applications.ProofFiles;
using Applications.StaffAlerts;
using Microsoft.Extensions.Logging;

namespace Applications.Services;

public class OrderManagementService : IOrderManagementService
{
    private readonly PosDbContext _db;
    private readonly IInventoryService _inventoryService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuditLogService _auditLogService;
    private readonly IPosStaffIdentity _staff;
    private readonly IProofUploadService? _proofUploads;
    private readonly IProofFileFinalizer? _proofFinalizer;
    private readonly IFileStorage? _fileStorage;
    private readonly ILogger<OrderManagementService> _logger;

    public OrderManagementService(PosDbContext db, IInventoryService inventoryService, IHttpContextAccessor httpContextAccessor, IAuditLogService auditLogService, IPosStaffIdentity staff,
        IProofUploadService? proofUploads = null, IProofFileFinalizer? proofFinalizer = null, IFileStorage? fileStorage = null, ILogger<OrderManagementService>? logger = null)
    {
        _db = db;
        _staff = staff;
        _inventoryService = inventoryService;
        _httpContextAccessor = httpContextAccessor;
        _auditLogService = auditLogService;
        _proofUploads = proofUploads;
        _proofFinalizer = proofFinalizer;
        _fileStorage = fileStorage;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderManagementService>.Instance;
    }

    // ────────────────────────────────────────────────────
    // POS-013: Online order approval queue
    // ────────────────────────────────────────────────────

    public async Task<List<OrderManagementResponseDto>> GetPendingApprovalAsync()
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();

        var query = _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .Where(o => o.OrderSource == "Ecommerce" && (o.OrderStatus == "Pending" || o.OrderStatus == "Awaiting Stock"))
            .AsQueryable();

        if (currentUser != null && !PosRoleAuthorization.IsAdministrator(currentUser))
        {
            var allowedLocations = currentUser.LocationIds;
            query = query.Where(order =>
                order.LocationId.HasValue && allowedLocations.Contains(order.LocationId.Value));
        }

        var orders = await query.OrderBy(o => o.CreatedAt).ToListAsync();

        return orders.Select(MapToResponse).ToList();
    }

    public async Task<OrderManagementResponseDto?> ApproveOrderAsync(int orderId, ApproveOrderDto dto)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if ((currentUser?.SubRole == "Cashier" || currentUser?.SubRole == "OrderManager") && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId)
            throw new InvalidOperationException("You are not authorized to approve this order for another location.");
        
        if (currentUser?.SubRole == "Cashier" && order.OrderSource == "Ecommerce")
            throw new InvalidOperationException("Cashiers are not authorized to approve ecommerce orders.");

        if (order.OrderStatus != "Pending" && order.OrderStatus != "Awaiting Stock")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already {order.OrderStatus}. Only Pending or Awaiting Stock orders can be approved.");

        var currentStatus = order.OrderStatus;
        var now = DateTime.UtcNow;
        order.OrderStatus = "Processing";
        order.ApprovedBy = await _staff.CurrentStaffIdAsync() ?? dto.ApprovedBy;
        order.ApprovedAt = now;
        order.UpdatedAt = now;

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Processing", dto.ApprovedBy, "Order approved");
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();
        await AuditOrderAsync(order, history, "order_approved", "approved");

        // Deduct stock for pre-orders upon transitioning to "Processing"
        if (order.IsPreorder && order.LocationId.HasValue)
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.DeductStockAsync(item.VariationId, order.LocationId.Value, item.Quantity, order.OrderId, "Sale", order.OrderNumber);
                }
                catch (InvalidOperationException ex)
                {
                    System.Console.WriteLine($"ApproveOrderAsync: Stock deduction skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                }
            }
        }

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> RejectOrderAsync(int orderId, RejectOrderDto dto)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if ((currentUser?.SubRole == "Cashier" || currentUser?.SubRole == "OrderManager") && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId)
            throw new InvalidOperationException("You are not authorized to reject this order for another location.");

        if (currentUser?.SubRole == "Cashier" && order.OrderSource == "Ecommerce")
            throw new InvalidOperationException("Cashiers are not authorized to reject ecommerce orders.");

        if (order.OrderStatus != "Pending" && order.OrderStatus != "Awaiting Stock")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already {order.OrderStatus}. Only Pending or Awaiting Stock orders can be rejected.");

        // Restore reserved stock back to location (e.g. Commissary 999) on cancellation/rejection, ONLY if it was previously deducted
        bool shouldRestoreStock = order.LocationId.HasValue && (!order.IsPreorder || (order.IsPreorder && order.OrderStatus != "Awaiting Stock"));

        var currentStatus = order.OrderStatus;
        var now = DateTime.UtcNow;
        order.OrderStatus = "Cancelled";
        order.ApprovedBy = await _staff.CurrentStaffIdAsync() ?? dto.RejectedBy;
        order.RejectionRemarks = dto.RejectionRemarks;
        order.UpdatedAt = now;

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Cancelled", dto.RejectedBy, $"Order rejected: {dto.RejectionRemarks}");
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();
        await AuditOrderAsync(order, history, "order_rejected", "rejected");

        if (shouldRestoreStock)
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity, order.OrderId, "Return", order.OrderNumber);
                }
                catch (InvalidOperationException ex)
                {
                    // Stock record may not be initialized/tracked — log it
                    System.Console.WriteLine($"RejectOrderAsync: Stock restoration skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                }
            }
        }

        return MapToResponse(order);
    }

    // ────────────────────────────────────────────────────
    // POS-014: Unified order list with filters
    // ────────────────────────────────────────────────────

    // Orders someone still has to act on; the order list always includes them, however old.
    private static readonly string[] OpenOrderStatuses =
        ["Pending", "Awaiting Stock", "Processing", "Paid", "Shipped", "Delivered", "Refund Requested", "Void Requested"];

    public async Task<List<OrderManagementResponseDto>> GetAllOrdersAsync(OrderFilterDto filter)
    {
        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        var query = _db.Orders.AsQueryable();

        if (currentUser is { IsSuperUser: false })
        {
            var allowedLocations = currentUser.LocationIds;
            if (allowedLocations.Length == 0 ||
                (filter.LocationId.HasValue && !allowedLocations.Contains(filter.LocationId.Value)))
            {
                return [];
            }

            query = query.Where(order =>
                order.LocationId.HasValue && allowedLocations.Contains(order.LocationId.Value));
            if (filter.LocationId.HasValue)
            {
                query = query.Where(order => order.LocationId == filter.LocationId.Value);
            }
        }
        else if (filter.LocationId.HasValue && filter.LocationId.Value > 0)
            query = query.Where(order => order.LocationId == filter.LocationId.Value);

        if (!string.IsNullOrWhiteSpace(filter.OrderStatus))
            query = query.Where(o => o.OrderStatus == filter.OrderStatus);

        if (!string.IsNullOrWhiteSpace(filter.OrderSource))
            query = query.Where(o => o.OrderSource == filter.OrderSource);

        if (!string.IsNullOrWhiteSpace(filter.OrderType))
            query = query.Where(o => o.OrderType == filter.OrderType);

        // Npgsql maps CreatedAt to `timestamp with time zone` and REQUIRES UTC
        // DateTimes for comparisons. A filter value with Unspecified/Local kind
        // would throw at query time — coerce to UTC to be safe.
        if (filter.DateFrom.HasValue)
        {
            var from = DateTime.SpecifyKind(filter.DateFrom.Value, DateTimeKind.Utc);
            query = filter.IncludeOpen
                ? query.Where(o => o.CreatedAt >= from || OpenOrderStatuses.Contains(o.OrderStatus))
                : query.Where(o => o.CreatedAt >= from);
        }

        if (filter.DateTo.HasValue)
        {
            var to = DateTime.SpecifyKind(filter.DateTo.Value, DateTimeKind.Utc);
            query = query.Where(o => o.CreatedAt <= to);
        }

        var orders = await query
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        var response = orders.Select(MapToResponse).ToList();
        await AttachDeliveryConfirmationsAsync(response);
        return response;
    }

    // ────────────────────────────────────────────────────
    // POS-015: COD auto-mark Paid on delivery confirm
    // ────────────────────────────────────────────────────

    public async Task<OrderManagementResponseDto?> ConfirmDeliveryAsync(int orderId, int confirmedBy)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if ((currentUser?.SubRole == "Cashier" || currentUser?.SubRole == "OrderManager") && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId)
            throw new InvalidOperationException("You are not authorized to perform this action for other locations.");
            
        if (currentUser?.SubRole == "Cashier" && order.OrderSource == "Ecommerce")
            throw new InvalidOperationException("Cashiers are not authorized to confirm delivery for ecommerce orders.");

        if (order.PaymentMethod != "COD")
            throw new InvalidOperationException($"Only COD orders can be confirmed via delivery. This order uses {order.PaymentMethod}.");

        if (order.OrderStatus == "Completed")
            throw new InvalidOperationException($"Order {order.OrderNumber} is already completed.");

        var currentStatus = order.OrderStatus;
        var now = DateTime.UtcNow;
        order.OrderStatus   = "Completed";
        order.PaymentStatus = "Paid";
        order.ApprovedBy    = await _staff.CurrentStaffIdAsync() ?? confirmedBy;
        order.ApprovedAt    = now;
        order.UpdatedAt     = now;

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Completed", confirmedBy, "COD delivery confirmed");
        _db.Orders.Update(order);

        // POS-031: Write Payment record on delivery confirm
        var payment = new Payment
        {
            OrderId        = order.OrderId,
            AmountPaid     = order.TotalAmount,
            PaymentChannel = order.PaymentMethod,
            PaymentStatus  = "Success",
            PaidAt         = now
        };
        await _db.Payments.AddAsync(payment);

        await _db.SaveChangesAsync();
        await AuditOrderAsync(order, history, "delivery_confirmed", "confirmed the COD delivery of");

        // POS-011: Auto deduct stock per item at this location (only if not already deducted for Ecommerce orders at checkout)
        if (order.LocationId.HasValue && !string.Equals(order.OrderSource, "Ecommerce", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.DeductStockAsync(item.VariationId, order.LocationId.Value, item.Quantity, order.OrderId, "Sale", order.OrderNumber);
                }
                catch (InvalidOperationException)
                {
                    // Stock may not be tracked for this item/location — log and continue
                }
            }
        }

        return MapToResponse(order);
    }

    // ────────────────────────────────────────────────────
    // Institutional delivery confirmation (Shipped → Completed with proof)
    // ────────────────────────────────────────────────────

    // Shrunk JPEGs are ~100–300 KB as data URLs; anything far past that wasn't shrunk.
    private const int MaxProofImageLength = 3_000_000;

    public async Task<OrderManagementResponseDto?> ConfirmInstitutionalDeliveryAsync(int orderId, ConfirmInstitutionalDeliveryDto dto)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser != null && !PosRoleAuthorization.IsManager(currentUser))
            throw new InvalidOperationException("Only Order Managers and Admins can confirm deliveries.");

        var proof = ResolveProof(order.OrderId, ProofKinds.Delivery, dto.ProofImage, dto.ProofReference);
        // A resubmission of a save that already committed (lost response, double click) changes nothing.
        if (proof.Reference is { } resent && await ProofAlreadySavedAsync(order.OrderId, ProofKinds.Delivery, resent.FileId))
            return await ReloadResponseAsync(order.OrderId);

        if (order.OrderType != "Institutional")
            throw new InvalidOperationException($"Order {order.OrderNumber} is not an institutional order.");

        if (order.OrderStatus != "Shipped")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Only Shipped orders can be confirmed as delivered.");

        if (order.PaymentStatus == "Partial")
            throw new InvalidOperationException($"Order {order.OrderNumber} still has an unpaid balance. Complete the remaining GCash payment first.");

        if (await _db.OrderDeliveryConfirmations.AnyAsync(c => c.OrderId == orderId))
            throw new InvalidOperationException($"Delivery for Order {order.OrderNumber} has already been confirmed.");

        var receivedBy = dto.ReceivedBy?.Trim() ?? string.Empty;
        if (receivedBy.Length == 0)
            throw new InvalidOperationException("Enter who received the order.");
        if (receivedBy.Length > 150)
            throw new InvalidOperationException("Received by must not exceed 150 characters.");

        var deliveredAt = dto.DeliveredAt.Kind switch
        {
            DateTimeKind.Utc => dto.DeliveredAt,
            DateTimeKind.Local => dto.DeliveredAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dto.DeliveredAt, DateTimeKind.Utc),
        };
        var now = DateTime.UtcNow;
        // A little slack for clock drift between the browser and the server.
        if (deliveredAt > now.AddMinutes(5))
            throw new InvalidOperationException("Delivered on can't be in the future.");
        if (deliveredAt < order.CreatedAt.AddMinutes(-1))
            throw new InvalidOperationException("Delivered on can't be before the order date.");

        var courierReference = string.IsNullOrWhiteSpace(dto.CourierReference) ? null : dto.CourierReference.Trim();
        if (courierReference?.Length > 500)
            throw new InvalidOperationException("Courier reference must not exceed 500 characters.");

        var notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
        if (notes?.Length > 1000)
            throw new InvalidOperationException("Notes must not exceed 1000 characters.");

        var confirmedByName = currentUser?.DisplayName ?? currentUser?.Username;
        var confirmation = new OrderDeliveryConfirmation
        {
            OrderId          = order.OrderId,
            ReceivedBy       = receivedBy,
            DeliveredAt      = deliveredAt,
            CourierReference = courierReference,
            ProofImage       = proof.Image,
            ProofFileId      = proof.Reference?.FileId,
            Notes            = notes,
            ConfirmedBy      = dto.ConfirmedBy,
            ConfirmedByName  = string.IsNullOrWhiteSpace(confirmedByName) ? null : confirmedByName,
            CreatedAt        = now
        };
        await _db.OrderDeliveryConfirmations.AddAsync(confirmation);

        var currentStatus = order.OrderStatus;
        order.OrderStatus   = "Completed";
        order.PaymentStatus = "Paid";
        order.UpdatedAt     = now;

        // Same as a manual move to Completed: make sure the sale has a payment on record.
        if (!order.Payments.Any(p => p.PaymentStatus == "Success"))
        {
            await _db.Payments.AddAsync(new Payment
            {
                OrderId        = order.OrderId,
                AmountPaid     = order.TotalAmount,
                PaymentChannel = order.PaymentMethod ?? "COD",
                PaymentStatus  = "Success",
                PaidAt         = now
            });
        }

        var byWhom = confirmation.ConfirmedByName != null ? $" by {confirmation.ConfirmedByName}" : "";
        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Completed", dto.ConfirmedBy,
            $"Delivery confirmed{byWhom}. Received by {receivedBy}.");

        // The File ID, its storage follow-up and the business change commit together.
        var proofTask = proof.Reference is { } ticket ? await AddProofTaskAsync(order.OrderId, ProofKinds.Delivery, ticket, now) : null;

        _db.Orders.Update(order);
        if (!await TrySaveProofCommitAsync(proof.Reference, order.OrderId, ProofKinds.Delivery))
            return await ReloadResponseAsync(order.OrderId);
        await AuditOrderAsync(order, history, "delivery_confirmed", "confirmed the delivery of");

        var response = MapToResponse(order);
        response.DeliveryConfirmation = MapDeliveryConfirmation(confirmation);
        response.DeliveryConfirmation.ProofPending = proofTask != null && !await FinalizeProofAsync(proofTask);
        return response;
    }

    public async Task<ProofImageResult?> GetDeliveryProofImageAsync(int orderId)
    {
        var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var row = await _db.OrderDeliveryConfirmations.AsNoTracking()
            .Where(c => c.OrderId == orderId)
            .Select(c => new { c.ProofFileId, c.ProofImage })
            .FirstOrDefaultAsync();
        return row == null ? null : await ResolveProofImageAsync(orderId, ProofKinds.Delivery, row.ProofFileId, row.ProofImage);
    }

    // ── Proof photos in the shared file-storage service ──

    private sealed record ProofInput(string? Image, ProofReference? Reference);

    // One representation per request: a legacy data URL, or a ready upload reference this user issued for this order.
    private ProofInput ResolveProof(int orderId, string kind, string? image, string? reference)
    {
        var hasImage = !string.IsNullOrWhiteSpace(image);
        var hasReference = !string.IsNullOrWhiteSpace(reference);
        if (hasImage && hasReference)
            throw new InvalidOperationException("Send the proof photo once: either the uploaded photo or the image, not both.");
        if (!hasReference) return new ProofInput(NormalizeProofImage(image), null);
        if (_proofUploads == null)
            throw new InvalidOperationException("Photo uploads are not available right now. Attach the photo again later.");
        return new ProofInput(null, _proofUploads.OpenReadyReference(orderId, kind, reference));
    }

    private Task<bool> ProofAlreadySavedAsync(int orderId, string kind, Guid fileId) =>
        _db.ProofFileTasks.AsNoTracking().AnyAsync(t => t.FileId == fileId && t.OrderId == orderId && t.Kind == kind);

    private async Task<ProofFileTask> AddProofTaskAsync(int orderId, string kind, ProofReference reference, DateTime now)
    {
        var task = new ProofFileTask
        {
            OrderId = orderId,
            Kind = kind,
            FileId = reference.FileId,
            Stage = ProofFileTaskStages.Pending,
            FileExpiresAt = reference.FileExpiresAt is { } expires ? DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime : null,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _db.ProofFileTasks.AddAsync(task);
        return task;
    }

    // Commits the business change. False when a concurrent submission of the same upload reference won
    // the race (unique File ID): nothing from this attempt is kept and the caller returns the saved result.
    private async Task<bool> TrySaveProofCommitAsync(ProofReference? reference, int orderId, string kind)
    {
        try
        {
            await _db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException) when (reference != null)
        {
            _db.ChangeTracker.Clear();
            if (await ProofAlreadySavedAsync(orderId, kind, reference.FileId)) return false;
            throw;
        }
    }

    // Links and promotes the File right away when storage answers; otherwise the background sweep retries.
    private async Task<bool> FinalizeProofAsync(ProofFileTask task)
    {
        if (_proofFinalizer == null) return false;
        try
        {
            return await _proofFinalizer.RunAsync(task.Id) == ProofFileTaskStages.Done;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Attaching proof File {FileId} for order {OrderId} will be retried.", task.FileId, task.OrderId);
            return false;
        }
    }

    private async Task<ProofImageResult?> ResolveProofImageAsync(int orderId, string kind, Guid? fileId, string? legacyImage)
    {
        if (fileId is not { } id)
            return string.IsNullOrEmpty(legacyImage) ? null : new ProofImageResult(legacyImage, ProofImageResult.LegacySource, false);

        if (_fileStorage == null)
            throw new ProofImageUnavailableException(StatusCodes.Status503ServiceUnavailable, "Photo storage is temporarily unavailable. Please try again.");

        var pending = await _db.ProofFileTasks.AsNoTracking().AnyAsync(t => t.OrderId == orderId && t.Kind == kind && t.FileId == id &&
            (t.Stage == ProofFileTaskStages.Pending || t.Stage == ProofFileTaskStages.Linked));
        try
        {
            var viewed = await _fileStorage.ViewAsync(id);
            var url = FileStorageValues.SafeStorageUrl(viewed.Url);
            if (viewed.Id != id || viewed.Status != FileStorageValues.Active || url == null)
                throw new FileStorageException(null, "Storage returned no usable view address.");
            return new ProofImageResult(url.ToString(), ProofImageResult.FileSource, pending);
        }
        catch (FileStorageException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            _logger.LogError("Proof File {FileId} saved on order {OrderId} ({Kind}) is missing from storage.", id, orderId, kind);
            throw new ProofImageUnavailableException(StatusCodes.Status404NotFound, "The saved proof photo is no longer available.");
        }
        catch (FileStorageException ex)
        {
            _logger.LogWarning("Viewing proof File {FileId} for order {OrderId} failed: storage status {Status}.", id, orderId, ex.Status?.ToString() ?? "unreachable");
            throw new ProofImageUnavailableException(StatusCodes.Status503ServiceUnavailable, "Photo storage is temporarily unavailable. Please try again.");
        }
    }

    // The order as the screen sees it, from what is committed now.
    private async Task<OrderManagementResponseDto?> ReloadResponseAsync(int orderId)
    {
        _db.ChangeTracker.Clear();
        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        var response = new List<OrderManagementResponseDto> { MapToResponse(order) };
        await AttachDeliveryConfirmationsAsync(response);
        return response[0];
    }

    // Adds delivery confirmations (without the photo) to a page of orders in one query.
    // Trims an optional proof photo and checks it's a reasonably sized image data URL.
    private static string? NormalizeProofImage(string? raw)
    {
        var image = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        if (image == null) return null;
        if (!image.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || !image.Contains(";base64,"))
            throw new InvalidOperationException("Proof photo must be an image.");
        if (image.Length > MaxProofImageLength)
            throw new InvalidOperationException("Proof photo is too large.");
        return image;
    }

    public async Task<ProofImageResult?> GetRefundProofImageAsync(int orderId)
    {
        var order = await _db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var row = await _db.OrderRefundProofs.AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .Select(p => new { p.ProofFileId, p.ProofImage })
            .FirstOrDefaultAsync();
        return row == null ? null : await ResolveProofImageAsync(orderId, ProofKinds.Refund, row.ProofFileId, row.ProofImage);
    }

    // Adds delivery confirmations and refund-proof flags (never the photos) to a page of orders.
    // Units refunded / awaiting refund approval per item, and their value.
    private async Task AttachRefundQuantitiesAsync(List<OrderManagementResponseDto> orders, List<int> orderIds)
    {
        var totals = await _db.RefundRequests
            .AsNoTracking()
            .Where(r => orderIds.Contains(r.OrderId) && (r.Status == "Approved" || r.Status == "Pending"))
            .GroupBy(r => new { r.OrderId, r.VariationId })
            .Select(g => new
            {
                g.Key.OrderId,
                g.Key.VariationId,
                Approved = g.Sum(r => r.Status == "Approved" ? r.QuantityToReturn : 0),
                Pending = g.Sum(r => r.Status == "Pending" ? r.QuantityToReturn : 0),
            })
            .ToListAsync();
        var byOrder = totals.ToLookup(t => t.OrderId);

        foreach (var order in orders)
        {
            var rows = byOrder[order.OrderId].ToList();
            if (rows.Count == 0)
            {
                // Refunds from before item refunds existed covered the whole order.
                if (order.OrderStatus == "Refunded")
                    foreach (var item in order.Items) item.RefundedQuantity = item.Quantity;
                else if (order.OrderStatus == "Refund Requested")
                    foreach (var item in order.Items) item.PendingRefundQuantity = item.Quantity;
            }
            else
            {
                // An order can list the same variation on more than one line: fill the lines in order.
                foreach (var row in rows)
                {
                    int approved = row.Approved, waiting = row.Pending;
                    foreach (var item in order.Items.Where(i => i.VariationId == row.VariationId))
                    {
                        item.RefundedQuantity = Math.Min(approved, item.Quantity);
                        approved -= item.RefundedQuantity;
                        item.PendingRefundQuantity = Math.Min(waiting, item.Quantity - item.RefundedQuantity);
                        waiting -= item.PendingRefundQuantity;
                    }
                }
            }

            order.RefundedAmount = order.Items.Sum(i => i.UnitPrice * i.RefundedQuantity);
            order.PendingRefundAmount = order.Items.Sum(i => i.UnitPrice * i.PendingRefundQuantity);
        }
    }

    private async Task AttachDeliveryConfirmationsAsync(List<OrderManagementResponseDto> orders)
    {
        var orderIds = orders.Select(o => o.OrderId).ToList();
        if (orderIds.Count == 0) return;

        // Flags only: photo bytes and signed addresses never go into lists.
        var withRefundProof = (await _db.OrderRefundProofs
            .AsNoTracking()
            .Where(p => orderIds.Contains(p.OrderId) && (p.ProofFileId != null || p.ProofImage != null))
            .Select(p => p.OrderId)
            .ToListAsync()).ToHashSet();
        var attaching = (await _db.ProofFileTasks
            .AsNoTracking()
            .Where(t => orderIds.Contains(t.OrderId) &&
                        (t.Stage == ProofFileTaskStages.Pending || t.Stage == ProofFileTaskStages.Linked))
            .Select(t => new { t.OrderId, t.Kind })
            .ToListAsync())
            .Select(t => (t.OrderId, t.Kind))
            .ToHashSet();
        foreach (var order in orders)
        {
            order.HasRefundProof = withRefundProof.Contains(order.OrderId);
            order.RefundProofPending = order.HasRefundProof && attaching.Contains((order.OrderId, ProofKinds.Refund));
        }

        await AttachRefundQuantitiesAsync(orders, orderIds);

        var names = await _staff.NamesAsync(orders
            .SelectMany(o => o.StatusHistory)
            .Where(h => h.ChangedBy.HasValue)
            .Select(h => h.ChangedBy!.Value));
        foreach (var history in orders.SelectMany(o => o.StatusHistory))
            if (history.ChangedBy.HasValue && names.TryGetValue(history.ChangedBy.Value, out var name))
                history.ChangedByName = name;

        // Flag requests the viewer filed, so the screen can disable Approve (the approve
        // endpoints refuse them anyway). The requester is whoever moved the order into the
        // requested status, the same person the refund rows record.
        var awaiting = orders.Where(o => o.OrderStatus is "Refund Requested" or "Void Requested").ToList();
        if (awaiting.Count > 0 && await _staff.CurrentStaffIdAsync() is int viewerId)
        {
            foreach (var order in awaiting)
            {
                var request = order.StatusHistory
                    .Where(h => h.NewStatus == order.OrderStatus)
                    .MaxBy(h => h.CreatedAt);
                order.RequestedByCurrentUser = request?.ChangedBy == viewerId;
            }
        }

        var confirmations = await _db.OrderDeliveryConfirmations
            .AsNoTracking()
            .Where(c => orderIds.Contains(c.OrderId))
            .Select(c => new
            {
                c.OrderId,
                Dto = new DeliveryConfirmationResponseDto
                {
                    ReceivedBy       = c.ReceivedBy,
                    DeliveredAt      = c.DeliveredAt,
                    CourierReference = c.CourierReference,
                    Notes            = c.Notes,
                    HasProofImage    = c.ProofImage != null || c.ProofFileId != null,
                    ConfirmedBy      = c.ConfirmedBy,
                    ConfirmedByName  = c.ConfirmedByName,
                    ConfirmedAt      = c.CreatedAt
                }
            })
            .ToDictionaryAsync(c => c.OrderId, c => c.Dto);

        foreach (var order in orders)
        {
            if (confirmations.TryGetValue(order.OrderId, out var confirmation))
            {
                confirmation.ProofPending = confirmation.HasProofImage && attaching.Contains((order.OrderId, ProofKinds.Delivery));
                order.DeliveryConfirmation = confirmation;
            }

            // Same rule the refund request enforces, so the screen can show it.
            var deadline = RefundPolicy.DeadlineUtc(order.OrderType, RefundPolicy.WindowStartUtc(
                order.OrderType,
                order.CreatedAt,
                order.StatusHistory.Select(h => (h.NewStatus, h.CreatedAt)),
                order.DeliveryConfirmation?.DeliveredAt));
            order.RefundDeadline = deadline;
        }
    }

    private static DeliveryConfirmationResponseDto MapDeliveryConfirmation(OrderDeliveryConfirmation c) => new()
    {
        ReceivedBy       = c.ReceivedBy,
        DeliveredAt      = c.DeliveredAt,
        CourierReference = c.CourierReference,
        Notes            = c.Notes,
        HasProofImage    = c.ProofImage != null || c.ProofFileId != null,
        ConfirmedBy      = c.ConfirmedBy,
        ConfirmedByName  = c.ConfirmedByName,
        ConfirmedAt      = c.CreatedAt
    };

    public async Task<OrderManagementResponseDto?> UpdateOrderStatusAsync(int orderId, UpdateOrderStatusDto dto)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser != null && !PosRoleAuthorization.IsManager(currentUser))
        {
            throw new InvalidOperationException("Only Order Managers and Admins are authorized to update the status of ecommerce orders.");
        }

        var targetStatus = dto.Status.Trim();
        var currentStatus = order.OrderStatus;

        if (string.Equals(targetStatus, currentStatus, StringComparison.OrdinalIgnoreCase))
        {
            return MapToResponse(order);
        }

        // Refunds only go through the refund request/approve flow (manager PIN, refund records,
        // refund window, no self-approval), never through a plain status change.
        if (targetStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase) ||
            targetStatus.Equals("Refund Requested", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Use Request Refund for Order {order.OrderNumber}; refunds can't be set through a status change.");

        var allowedStatuses = new[] { "Pending", "Awaiting Stock", "Processing", "Paid", "Shipped", "Delivered", "Completed", "Cancelled" };
        if (!allowedStatuses.Contains(targetStatus, System.StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid target status '{targetStatus}'.");
        }

        if (order.OrderType == "Institutional")
        {
            // Institutional orders close through Confirm Delivery so the proof is recorded.
            if (string.Equals(targetStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Ship Order {order.OrderNumber} and use Confirm Delivery to complete it.");

            // Shipped goods are already with the courier, so the order can't move back.
            var beforeShipping = new[] { "Pending", "Awaiting Stock", "Processing", "Paid" };
            if (currentStatus == "Shipped" && beforeShipping.Contains(targetStatus, System.StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Order {order.OrderNumber} has already shipped and can't go back to {targetStatus}.");

            // A confirmed delivery is final; refunds go through the refund request flow.
            if (currentStatus == "Completed")
                throw new InvalidOperationException($"Order {order.OrderNumber} has been delivered. Request a refund instead of changing its status.");
        }

        // Cancelling is only for institutional orders on a 50% GCash downpayment: the client
        // backs out, the downpayment is kept (non-refundable) and the goods go back to stock.
        // Paid orders use Void, Failed Delivery or Refund so the money has an audit trail.
        var isCancel = string.Equals(targetStatus, "Cancelled", StringComparison.OrdinalIgnoreCase);
        if (isCancel && !(order.OrderType == "Institutional" && order.PaymentStatus == "Partial"))
        {
            throw new InvalidOperationException($"Only institutional orders with a GCash downpayment can be cancelled. Use Void or Refund for Order {order.OrderNumber}.");
        }

        var requiresFullPayment = new[] { "Paid", "Shipped", "Delivered", "Completed" };
        if (order.PaymentStatus == "Partial" && requiresFullPayment.Contains(targetStatus, System.StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Order {order.OrderNumber} still has an unpaid balance. Complete the remaining GCash payment first.");
        }

        var now = DateTime.UtcNow;
        order.OrderStatus = targetStatus;
        order.ApprovedBy = await _staff.CurrentStaffIdAsync() ?? dto.UpdatedBy;
        order.UpdatedAt = now;

        var historyRemark = "Status updated via panel";
        if (isCancel)
        {
            var kept = order.Payments.Where(p => p.PaymentStatus == "Success").Sum(p => p.AmountPaid);
            order.PaymentStatus = "Forfeited";
            order.ApprovedAt = now; // Sales reports date the forfeited downpayment from this.
            order.RejectionRemarks = $"Cancelled by client. Downpayment of ₱{kept:N2} is non-refundable.";
            historyRemark = order.RejectionRemarks;
        }

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, targetStatus, dto.UpdatedBy, historyRemark);

        if (string.Equals(targetStatus, "Processing", StringComparison.OrdinalIgnoreCase))
        {
            order.ApprovedAt = now;
            // Deduct stock for pre-orders upon transitioning to "Processing"
            if (order.IsPreorder && order.LocationId.HasValue)
            {
                foreach (var item in order.OrderItems)
                {
                    try
                    {
                        await _inventoryService.DeductStockAsync(item.VariationId, order.LocationId.Value, item.Quantity, order.OrderId, "Sale", order.OrderNumber);
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Console.WriteLine($"UpdateOrderStatusAsync: Stock deduction skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                    }
                }
            }
        }
        else if (string.Equals(targetStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            // Restore reserved stock if cancelled, ONLY if it was previously deducted
            bool shouldRestoreStock = order.LocationId.HasValue && (!order.IsPreorder || (order.IsPreorder && currentStatus != "Awaiting Stock"));
            if (shouldRestoreStock)
            {
                foreach (var item in order.OrderItems)
                {
                    try
                    {
                        await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity, order.OrderId, "Return", order.OrderNumber);
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Console.WriteLine($"UpdateOrderStatusAsync: Stock restoration skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                    }
                }
            }
        }
        // No longer reachable: "Refunded" is refused above (refunds use ApproveRefundAsync).
        // Kept so the old behaviour is visible if this is ever revisited.
        else if (string.Equals(targetStatus, "Refunded", StringComparison.OrdinalIgnoreCase))
        {
            if (order.LocationId.HasValue)
            {
                var isUnsellable = order.RejectionRemarks != null &&
                    (order.RejectionRemarks.Contains("(Unsellable)", StringComparison.OrdinalIgnoreCase) ||
                     order.RejectionRemarks.StartsWith("Damaged/Expired", StringComparison.OrdinalIgnoreCase));

                foreach (var item in order.OrderItems)
                {
                    try
                    {
                        if (isUnsellable)
                        {
                            await _inventoryService.RecordStockLossAsync(
                                item.VariationId,
                                order.LocationId.Value,
                                item.Quantity,
                                order.OrderId,
                                order.OrderNumber,
                                order.RejectionRemarks);
                        }
                        else
                        {
                            await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity, order.OrderId, "Refund", order.OrderNumber);
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        System.Console.WriteLine($"UpdateOrderStatusAsync: Stock restoration skipped for VariationId {item.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                    }
                }
            }
        }
        else if (string.Equals(targetStatus, "Completed", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(targetStatus, "Delivered", StringComparison.OrdinalIgnoreCase))
        {
            order.PaymentStatus = "Paid";
            
            var hasPayment = await _db.Payments.AnyAsync(p => p.OrderId == order.OrderId && p.PaymentStatus == "Success");
            if (!hasPayment)
            {
                var payment = new Payment
                {
                    OrderId        = order.OrderId,
                    AmountPaid     = order.TotalAmount,
                    PaymentChannel = order.PaymentMethod ?? "COD",
                    PaymentStatus  = "Success",
                    PaidAt         = now
                };
                await _db.Payments.AddAsync(payment);
            }
        }

        _db.Orders.Update(order);
        await _db.SaveChangesAsync();
        await AuditOrderAsync(order, history,
            isCancel ? "order_cancelled" : "status_changed",
            isCancel ? "cancelled" : $"changed the status to {targetStatus} for");

        return MapToResponse(order);
    }

    // ────────────────────────────────────────────────────
    // Institutional GCash downpayment: confirm remaining balance
    // ────────────────────────────────────────────────────

    public async Task<OrderManagementResponseDto?> CompletePaymentAsync(int orderId, int confirmedBy)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        if (order.PaymentStatus != "Partial")
            throw new InvalidOperationException($"Order {order.OrderNumber} has no outstanding balance.");

        if (order.OrderStatus is "Cancelled" or "Void Requested" or "Voided" or "Refund Requested" or "Refunded")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus} and can no longer be paid.");

        var alreadyPaid = order.Payments.Where(p => p.PaymentStatus == "Success").Sum(p => p.AmountPaid);
        var balance = order.TotalAmount - alreadyPaid;
        var now = DateTime.UtcNow;

        if (balance > 0)
        {
            order.Payments.Add(new Payment
            {
                OrderId        = order.OrderId,
                AmountPaid     = balance,
                PaymentChannel = order.PaymentMethod,
                PaymentStatus  = "Success",
                PaidAt         = now
            });
        }

        var currentStatus = order.OrderStatus;
        order.PaymentStatus = "Paid";
        order.UpdatedAt = now;

        // Fully paid institutional orders move to Paid (ready for shipment).
        if (currentStatus == "Processing")
            order.OrderStatus = "Paid";

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, order.OrderStatus, confirmedBy,
            $"Remaining GCash balance of ₱{balance:N2} confirmed in wallet. Order fully paid.");

        _db.Orders.Update(order);
        await _db.SaveChangesAsync();
        await AuditOrderAsync(order, history, "payment_completed", "completed the payment for");

        return MapToResponse(order);
    }

    // ────────────────────────────────────────────────────
    // EC-012: Order progress tracking for progress bar
    // ────────────────────────────────────────────────────

    public async Task<OrderTrackingDto?> GetOrderTrackingAsync(int orderId)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderId == orderId);

        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        return new OrderTrackingDto
        {
            OrderId       = order.OrderId,
            OrderNumber   = order.OrderNumber,
            OrderSource   = order.OrderSource,
            OrderType     = order.OrderType,
            OrderStatus   = order.OrderStatus,
            PaymentMethod = order.PaymentMethod,
            PaymentStatus = order.PaymentStatus,
            IsPreorder    = order.IsPreorder,
            CreatedAt     = order.CreatedAt,
            UpdatedAt     = order.UpdatedAt,
            ApprovedAt    = order.ApprovedAt
        };
    }

    // ────────────────────────────────────────────────────
    // POS-016 & 017: Refunds
    // ────────────────────────────────────────────────────

    private const string FailedDeliveryReason = "Item not delivered";

    public async Task<OrderManagementResponseDto?> RequestRefundAsync(int orderId, string reason, string? proofImage, IReadOnlyList<RefundItemDto>? items = null, string? proofReference = null)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        var proof = ResolveProof(order.OrderId, ProofKinds.Refund, proofImage, proofReference);
        // A resubmission of a request that already committed (lost response, double click) changes nothing.
        if (proof.Reference is { } resent && await ProofAlreadySavedAsync(order.OrderId, ProofKinds.Refund, resent.FileId))
            return await ReloadResponseAsync(order.OrderId);

        if (order.OrderStatus == "Refund Requested")
            throw new InvalidOperationException($"Order {order.OrderNumber} already has a refund request waiting for a manager.");

        // One approved refund per order: after a partial refund the rest of the sale stands.
        if (await _db.RefundRequests.AnyAsync(r => r.OrderId == order.OrderId && r.Status == "Approved"))
            throw new InvalidOperationException($"Order {order.OrderNumber} has already been refunded once, so it can't be refunded again.");

        if (order.OrderStatus != "Completed" && order.PaymentStatus != "Paid")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus} (Payment: {order.PaymentStatus}). Only Paid or Completed orders can be refunded.");

        // 50% GCash downpayments can only be refunded once the balance is paid.
        if (order.PaymentStatus == "Partial")
            throw new InvalidOperationException($"Order {order.OrderNumber} still has an unpaid balance. Complete the remaining GCash payment before requesting a refund.");

        var hasProof = proof.Image != null || proof.Reference != null;
        var isPaidInstitutional = order.OrderType == "Institutional" &&
            (string.Equals(order.PaymentMethod, "Cash", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(order.PaymentMethod, "GCash", StringComparison.OrdinalIgnoreCase));

        // A shipped institutional order that Lalamove failed to deliver is refunded with
        // "Item not delivered" (restocked, since the courier returns the goods).
        var isFailedDelivery = order.OrderType == "Institutional" && order.OrderStatus == "Shipped";
        var isDeliveredInstitutional = order.OrderType == "Institutional" &&
            (string.Equals(order.OrderStatus, "Completed", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(order.OrderStatus, "Delivered", StringComparison.OrdinalIgnoreCase));
        if (isPaidInstitutional && !isFailedDelivery && !isDeliveredInstitutional)
            throw new InvalidOperationException($"Paid institutional order {order.OrderNumber} cannot request a standard refund before delivery is confirmed.");

        if (isFailedDelivery && !string.Equals(reason?.Trim(), FailedDeliveryReason, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Order {order.OrderNumber} hasn't been delivered yet. Only a failed delivery can be refunded before delivery is confirmed.");

        // Paid institutional customers can only ask for a refund once the order is delivered.
        if (isPaidInstitutional && !isFailedDelivery && !isDeliveredInstitutional)
            throw new InvalidOperationException($"Order {order.OrderNumber} hasn't been delivered yet. Paid institutional orders can only be refunded after delivery is confirmed.");
        // Required proof must come with this request, never from an earlier rejected one.
        if ((isPaidInstitutional || isFailedDelivery) && !hasProof)
            throw new InvalidOperationException(isFailedDelivery
                ? "Attach proof that the item wasn't delivered (e.g. Lalamove's failed-delivery screen)."
                : "Attach a photo as proof for refunds on paid institutional orders.");

        // Refund window (RefundPolicy): food can only be returned shortly after the sale or delivery.
        // A failed delivery never reached the customer, so it has no window.
        if (!isFailedDelivery)
        {
            var deliveredAt = await _db.OrderDeliveryConfirmations
                .Where(c => c.OrderId == order.OrderId)
                .Select(c => (DateTime?)c.DeliveredAt)
                .FirstOrDefaultAsync();
            var deadline = RefundPolicy.DeadlineUtc(order.OrderType, RefundPolicy.WindowStartUtc(
                order.OrderType,
                order.CreatedAt,
                order.StatusHistory.Select(h => (h.NewStatus, h.CreatedAt)),
                deliveredAt));
            if (deadline.HasValue && DateTime.UtcNow >= deadline.Value)
                throw new InvalidOperationException($"The refund window for order {order.OrderNumber} ended on {RefundPolicy.Describe(deadline.Value)}.");
        }

        // Which items come back, and how many. A failed delivery always returns everything.
        var lines = await ResolveRefundLinesAsync(order, isFailedDelivery ? null : items);

        var now = DateTime.UtcNow;
        ProofFileTask? proofTask = null;
        if (hasProof)
        {
            // A new request after a rejection replaces the earlier photo, in this same commit.
            var existing = await _db.OrderRefundProofs.FirstOrDefaultAsync(p => p.OrderId == order.OrderId);
            if (existing == null)
            {
                existing = new OrderRefundProof { OrderId = order.OrderId };
                await _db.OrderRefundProofs.AddAsync(existing);
            }
            else if (existing.ProofFileId is { } superseded && superseded != proof.Reference?.FileId)
            {
                // Retire the older File's order link once the new photo is attached (exact unlink, never delete).
                var oldTask = await _db.ProofFileTasks.FirstOrDefaultAsync(t => t.FileId == superseded);
                if (oldTask != null && oldTask.Stage is not (ProofFileTaskStages.Retired or ProofFileTaskStages.Expired))
                {
                    oldTask.Stage = ProofFileTaskStages.Detach;
                    oldTask.NextAttemptAt = now;
                    oldTask.UpdatedAt = now;
                }
            }
            existing.ProofImage = proof.Image;
            existing.ProofFileId = proof.Reference?.FileId;
            existing.CreatedAt = now;

            if (proof.Reference is { } ticket)
                proofTask = await AddProofTaskAsync(order.OrderId, ProofKinds.Refund, ticket, now);
        }

        var trimmedReason = reason.Trim();
        var requestedBy = await _staff.CurrentStaffIdAsync() ?? 0;
        foreach (var line in lines)
        {
            await _db.RefundRequests.AddAsync(new RefundRequest
            {
                OrderId = order.OrderId,
                VariationId = line.VariationId,
                LocationId = order.LocationId ?? 0,
                QuantityToReturn = line.Quantity,
                Reason = trimmedReason,
                RefundMethod = RefundMethods.For(order.PaymentMethod),
                Status = "Pending",
                RequestedBy = requestedBy,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        var currentStatus = order.OrderStatus;
        order.OrderStatus = "Refund Requested";
        order.RejectionRemarks = trimmedReason; // Store reason here as agreed
        order.UpdatedAt = now;

        // Partial unless this request brings back every unit bought.
        var alreadyRefunded = await LoadRefundQuantitiesAsync(order.OrderId);
        var requestIsPartial = order.OrderItems
            .GroupBy(i => i.VariationId)
            .Any(g => g.Sum(i => i.Quantity)
                > (alreadyRefunded.TryGetValue(g.Key, out var q) ? q.Approved : 0)
                  + lines.Where(l => l.VariationId == g.Key).Sum(l => l.Quantity));

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Refund Requested", 1,
            $"{trimmedReason} ({DescribeRefundLines(order, lines)})");
        // One alert for the request however many items it covers, saved with it.
        RaiseOrderAlert(StaffAlertKinds.RefundRequested, order, history, CurrentPortalUserId(), requestIsPartial);
        _db.Orders.Update(order);
        if (!await TrySaveProofCommitAsync(proof.Reference, order.OrderId, ProofKinds.Refund))
            return await ReloadResponseAsync(order.OrderId);
        await AuditOrderAsync(order, history, "refund_requested",
            requestIsPartial ? "requested a partial refund for" : "requested a full refund for",
            partial: requestIsPartial);

        var response = MapToResponse(order);
        response.HasRefundProof = hasProof || await _db.OrderRefundProofs.AnyAsync(p => p.OrderId == order.OrderId);
        response.RefundProofPending = proofTask != null && !await FinalizeProofAsync(proofTask);
        return response;
    }

    private sealed record RefundLine(int VariationId, int Quantity);

    // Units of each variation in the order that are already refunded or waiting for approval.
    private async Task<Dictionary<int, (int Approved, int Pending)>> LoadRefundQuantitiesAsync(int orderId)
    {
        var rows = await _db.RefundRequests
            .AsNoTracking()
            .Where(r => r.OrderId == orderId && (r.Status == "Approved" || r.Status == "Pending"))
            .GroupBy(r => r.VariationId)
            .Select(g => new
            {
                VariationId = g.Key,
                Approved = g.Sum(r => r.Status == "Approved" ? r.QuantityToReturn : 0),
                Pending = g.Sum(r => r.Status == "Pending" ? r.QuantityToReturn : 0),
            })
            .ToListAsync();
        return rows.ToDictionary(r => r.VariationId, r => (r.Approved, r.Pending));
    }

    // Checks the requested items against what was bought and what's already refunded.
    // No items means "everything not refunded yet".
    private async Task<List<RefundLine>> ResolveRefundLinesAsync(Order order, IReadOnlyList<RefundItemDto>? items)
    {
        var purchased = order.OrderItems
            .GroupBy(i => i.VariationId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var already = await LoadRefundQuantitiesAsync(order.OrderId);
        int Remaining(int variationId) =>
            purchased.GetValueOrDefault(variationId)
            - (already.TryGetValue(variationId, out var q) ? q.Approved + q.Pending : 0);

        List<RefundLine> lines;
        if (items == null || items.Count == 0)
        {
            lines = purchased.Keys
                .Select(v => new RefundLine(v, Remaining(v)))
                .Where(l => l.Quantity > 0)
                .ToList();
        }
        else
        {
            lines = items
                .GroupBy(i => i.VariationId)
                .Select(g => new RefundLine(g.Key, g.Sum(i => i.Quantity)))
                .ToList();
            foreach (var line in lines)
            {
                if (!purchased.ContainsKey(line.VariationId))
                    throw new InvalidOperationException($"That item isn't part of order {order.OrderNumber}.");
                if (line.Quantity <= 0)
                    throw new InvalidOperationException("Each item being refunded needs a quantity of at least 1.");
                var left = Remaining(line.VariationId);
                if (line.Quantity > left)
                    throw new InvalidOperationException(left <= 0
                        ? $"{ItemLabel(order, line.VariationId)} has already been refunded."
                        : $"Only {left} of {ItemLabel(order, line.VariationId)} can still be refunded.");
            }
        }

        if (lines.Count == 0)
            throw new InvalidOperationException($"Everything in order {order.OrderNumber} has already been refunded.");
        return lines;
    }

    private static string ItemLabel(Order order, int variationId)
    {
        var item = order.OrderItems.FirstOrDefault(i => i.VariationId == variationId);
        var product = item?.ProductVariation?.Product?.ProductName;
        var variation = VariationLabel(item?.ProductVariation?.VariationName);
        return product == null ? "this item" : string.IsNullOrWhiteSpace(variation) ? product : $"{product} ({variation})";
    }

    // "Jar · 300g" from "UJM-SM-300|Jar|300g" (the SKU is left out); names without parts as they are.
    private static string? VariationLabel(string? variationName)
    {
        if (string.IsNullOrWhiteSpace(variationName) || !variationName.Contains('|')) return variationName;
        var parts = variationName.Split('|').Select(p => p.Trim()).ToArray();
        return string.Join(" · ", parts.Skip(1).Where(p => p.Length > 0));
    }

    private static decimal UnitPriceOf(Order order, int variationId) =>
        order.OrderItems.FirstOrDefault(i => i.VariationId == variationId)?.UnitPrice ?? 0m;

    // e.g. "2 × Ube Halaya (Large), 1 × Strawberry Jam; ₱540.00"
    private static string DescribeRefundLines(Order order, IEnumerable<RefundLine> lines)
    {
        var list = lines.ToList();
        var amount = list.Sum(l => UnitPriceOf(order, l.VariationId) * l.Quantity);
        var items = string.Join(", ", list.Select(l => $"{l.Quantity} × {ItemLabel(order, l.VariationId)}"));
        return $"{items}; ₱{amount:N2}";
    }

    public async Task<OrderManagementResponseDto?> ApproveRefundAsync(int orderId, int approvedBy, string? pinCode)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
            
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus != "Refund Requested")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Only Refund Requested orders can be approved.");

        if (string.IsNullOrWhiteSpace(pinCode))
            throw new InvalidOperationException("Manager PIN is required to approve a refund.");

        var authUserId = currentUser?.PortalUserId ?? (currentUser?.Id != Guid.Empty ? currentUser?.Id.ToString() : null);
        if (!string.IsNullOrEmpty(authUserId))
        {
            var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
            if (userPin == null || userPin.PinCode != pinCode)
                throw new InvalidOperationException("Invalid Manager PIN.");
        }
        else
        {
            throw new InvalidOperationException("Unable to identify manager to validate PIN.");
        }

        var now = DateTime.UtcNow;
        var already = await LoadRefundQuantitiesAsync(orderId);
        var pending = await _db.RefundRequests
            .Where(r => r.OrderId == orderId && r.Status == "Pending")
            .ToListAsync();

        // Four eyes: whoever asked for the refund can't approve it. Requests from before
        // requesters were recorded (0 / 1) never match a staff number, so they're unaffected.
        var approverId = await _staff.CurrentStaffIdAsync();
        if (approverId.HasValue && pending.Any(r => r.RequestedBy == approverId.Value))
            throw new InvalidOperationException("You requested this refund, so a different manager has to approve it.");

        // Requests filed before item refunds existed have no item rows: refund what's left.
        if (pending.Count == 0)
        {
            foreach (var group in order.OrderItems.GroupBy(i => i.VariationId))
            {
                var left = group.Sum(i => i.Quantity) - (already.TryGetValue(group.Key, out var q) ? q.Approved : 0);
                if (left <= 0) continue;
                var row = new RefundRequest
                {
                    OrderId = order.OrderId,
                    VariationId = group.Key,
                    LocationId = order.LocationId ?? 0,
                    QuantityToReturn = left,
                    Reason = order.RejectionRemarks ?? "Refund",
                    RefundMethod = RefundMethods.For(order.PaymentMethod),
                    Status = "Pending",
                    RequestedBy = 0, // filed before requesters were recorded
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                await _db.RefundRequests.AddAsync(row);
                pending.Add(row);
            }
        }

        foreach (var req in pending)
        {
            req.Status = "Approved";
            req.ApprovedBy = await _staff.CurrentStaffIdAsync() ?? approvedBy;
            req.ApprovedAt = now;
            req.UpdatedAt = now;
        }

        // The order is Refunded only once every unit has come back. Otherwise it returns to
        // the status it had before the request, with the refunded units recorded per item.
        var refundedAfter = order.OrderItems
            .GroupBy(i => i.VariationId)
            .ToDictionary(
                g => g.Key,
                g => (Bought: g.Sum(i => i.Quantity),
                      Refunded: (already.TryGetValue(g.Key, out var q) ? q.Approved : 0)
                          + pending.Where(r => r.VariationId == g.Key).Sum(r => r.QuantityToReturn)));
        var fullyRefunded = refundedAfter.Values.All(v => v.Refunded >= v.Bought);
        var summary = DescribeRefundLines(order, pending.Select(r => new RefundLine(r.VariationId, r.QuantityToReturn)));
        var requester = await RequesterPortalIdAsync(orderId, "Refund Requested");

        var currentStatus = order.OrderStatus;
        OrderStatusHistory history;
        if (fullyRefunded)
        {
            order.OrderStatus = "Refunded";
            order.ApprovedBy = await _staff.CurrentStaffIdAsync() ?? approvedBy;
            order.ApprovedAt = now;
            history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Refunded", approvedBy, $"Refund approved ({summary})");
        }
        else
        {
            var previousStatus = await PreviousStatusBeforeRefundRequestAsync(orderId);
            order.OrderStatus = previousStatus;
            history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, previousStatus, approvedBy, $"Partial refund approved ({summary})");
        }
        order.UpdatedAt = now;
        _db.Orders.Update(order);
        RaiseOrderAlert(StaffAlertKinds.RefundApproved, order, history, requester.PortalUserId, partial: !fullyRefunded);

        await _db.SaveChangesAsync();
        // One manager decision, however many lines it covers.
        await AuditOrderAsync(order, history, "refund_approved",
            fullyRefunded ? "approved a full refund for" : "approved a partial refund for",
            partial: !fullyRefunded);

        // POS-017: Auto restore stock per refunded item. Unsellable goods (in-store "Damaged/Expired
        // (Unsellable)", institutional "Damaged (Unsellable)") were refunded but can't be
        // resold, so they stay out of stock. Institutional "Item not delivered" is restocked
        // because the courier returns the goods.
        if (order.LocationId.HasValue)
        {
            foreach (var req in pending)
            {
                var isUnsellable =
                    req.Reason.Contains("(Unsellable)", StringComparison.OrdinalIgnoreCase) ||
                    req.Reason.StartsWith("Damaged/Expired", StringComparison.OrdinalIgnoreCase);
                try
                {
                    if (isUnsellable)
                        await _inventoryService.RecordStockLossAsync(req.VariationId, order.LocationId.Value, req.QuantityToReturn, order.OrderId, order.OrderNumber, req.Reason);
                    else
                        await _inventoryService.RestoreStockAsync(req.VariationId, order.LocationId.Value, req.QuantityToReturn, order.OrderId, "Refund", order.OrderNumber);
                }
                catch (InvalidOperationException ex)
                {
                    // Stock may not be tracked for this item/location.
                    System.Console.WriteLine($"ApproveRefundAsync: stock update skipped for VariationId {req.VariationId} at Location {order.LocationId.Value}: {ex.Message}");
                }
            }
        }

        return MapToResponse(order);
    }

    // The status an order had before its latest refund request (usually Completed; Shipped for a failed delivery).
    private async Task<string> PreviousStatusBeforeRefundRequestAsync(int orderId)
    {
        var record = await _db.OrderStatusHistories
            .Where(h => h.OrderId == orderId && h.NewStatus == "Refund Requested")
            .OrderByDescending(h => h.CreatedAt)
            .FirstOrDefaultAsync();
        return record?.OldStatus ?? "Completed";
    }

    public async Task<OrderManagementResponseDto?> RejectRefundAsync(int orderId, int rejectedBy, string reason, string? pinCode)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus != "Refund Requested")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Only Refund Requested orders can be rejected.");

        if (string.IsNullOrWhiteSpace(pinCode))
            throw new InvalidOperationException("Manager PIN is required to reject a refund.");

        var authUserId = currentUser?.PortalUserId ?? (currentUser?.Id != Guid.Empty ? currentUser?.Id.ToString() : null);
        if (!string.IsNullOrEmpty(authUserId))
        {
            var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
            if (userPin == null || userPin.PinCode != pinCode)
                throw new InvalidOperationException("Invalid Manager PIN.");
        }
        else
        {
            throw new InvalidOperationException("Unable to identify manager to validate PIN.");
        }

        // Revert to the status before the request (usually Completed; Shipped for a
        // rejected failed-delivery refund, so it can still be confirmed as delivered).
        var currentStatus = order.OrderStatus;
        var revertStatus = await PreviousStatusBeforeRefundRequestAsync(orderId);
        var requester = await RequesterPortalIdAsync(orderId, "Refund Requested");

        order.OrderStatus = revertStatus;
        order.ApprovedBy = await _staff.CurrentStaffIdAsync() ?? rejectedBy; // Log manager id
        order.RejectionRemarks = $"Refund Rejected: {reason}";
        order.UpdatedAt = DateTime.UtcNow;

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, revertStatus, rejectedBy, $"Refund rejected: {reason}");
        _db.Orders.Update(order);

        // Only the request being rejected; earlier approved refunds on this order stay approved.
        var refundRequests = await _db.RefundRequests.Where(r => r.OrderId == orderId && r.Status == "Pending").ToListAsync();
        foreach (var req in refundRequests)
        {
            req.Status = "Rejected";
            req.ApprovedBy = await _staff.CurrentStaffIdAsync() ?? rejectedBy;
            req.UpdatedAt = DateTime.UtcNow;
        }
        RaiseOrderAlert(StaffAlertKinds.RefundRejected, order, history, requester.PortalUserId, requester.Partial);

        await _db.SaveChangesAsync();
        // A saved decision to reject is a successful change.
        await AuditOrderAsync(order, history, "refund_rejected", "rejected the refund request for");

        return MapToResponse(order);
    }

    // ────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────

    public async Task<OrderManagementResponseDto?> RequestVoidAsync(int orderId, string reason)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus == "Void Requested" || order.OrderStatus == "Voided" || order.OrderStatus == "Cancelled" || order.OrderStatus == "Refund Requested" || order.OrderStatus == "Refunded")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Cannot request void.");

        if (await _db.RefundRequests.AnyAsync(r => r.OrderId == order.OrderId && r.Status == "Approved"))
            throw new InvalidOperationException($"Part of order {order.OrderNumber} has already been refunded, so it can't be voided. Request a refund for the remaining items instead.");

        var currentStatus = order.OrderStatus;
        order.OrderStatus = "Void Requested";
        order.RejectionRemarks = reason;
        order.UpdatedAt = DateTime.UtcNow;

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Void Requested", 1, reason);
        RaiseOrderAlert(StaffAlertKinds.VoidRequested, order, history, CurrentPortalUserId());
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();
        await AuditOrderAsync(order, history, "void_requested", "requested a void of");

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> ApproveVoidAsync(int orderId, int approvedBy, string? pinCode)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
            
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus != "Void Requested")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Only Void Requested orders can be approved.");

        if (string.IsNullOrWhiteSpace(pinCode))
            throw new InvalidOperationException("Manager PIN is required to approve a void.");

        var authUserId = currentUser?.PortalUserId ?? (currentUser?.Id != Guid.Empty ? currentUser?.Id.ToString() : null);
        if (!string.IsNullOrEmpty(authUserId))
        {
            var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
            if (userPin == null || userPin.PinCode != pinCode)
                throw new InvalidOperationException("Invalid Manager PIN.");
        }
        else
        {
            throw new InvalidOperationException("Unable to identify manager to validate PIN.");
        }

        // Four eyes: whoever asked for the void can't approve it.
        var voidApproverId = await _staff.CurrentStaffIdAsync();
        if (voidApproverId.HasValue)
        {
            var voidRequestedBy = await _db.OrderStatusHistories
                .Where(h => h.OrderId == orderId && h.NewStatus == "Void Requested")
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => h.ChangedBy)
                .FirstOrDefaultAsync();
            if (voidRequestedBy == voidApproverId)
                throw new InvalidOperationException("You requested this void, so a different manager has to approve it.");
        }

        var requester = await RequesterPortalIdAsync(orderId, "Void Requested");
        var currentStatus = order.OrderStatus;
        order.OrderStatus = "Voided";
        order.ApprovedBy = await _staff.CurrentStaffIdAsync() ?? approvedBy;
        order.ApprovedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, "Voided", approvedBy, "Void approved");
        RaiseOrderAlert(StaffAlertKinds.VoidApproved, order, history, requester.PortalUserId);
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();
        await AuditOrderAsync(order, history, "void_approved", "approved the void of");

        if (order.LocationId.HasValue)
        {
            foreach (var item in order.OrderItems)
            {
                try
                {
                    await _inventoryService.RestoreStockAsync(item.VariationId, order.LocationId.Value, item.Quantity, order.OrderId, "Void", order.OrderNumber);
                }
                catch (InvalidOperationException) {}
            }
        }

        return MapToResponse(order);
    }

    public async Task<OrderManagementResponseDto?> RejectVoidAsync(int orderId, int rejectedBy, string reason, string pinCode)
    {
        var order = await _db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.ProductVariation)
                    .ThenInclude(v => v.Product)
            .Include(o => o.Payments)
            .Include(o => o.Location)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
        if (order == null) return null;
        EnsureCurrentUserCanAccessOrderLocation(order);

        var currentUser = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (currentUser?.SubRole == "Cashier" && currentUser.LocationId.HasValue && order.LocationId != currentUser.LocationId && order.OrderSource != "Ecommerce")
            throw new InvalidOperationException("You are not authorized to perform this action for other locations or ecommerce orders.");

        if (order.OrderStatus != "Void Requested")
            throw new InvalidOperationException($"Order {order.OrderNumber} is {order.OrderStatus}. Only Void Requested orders can be rejected.");

        if (string.IsNullOrWhiteSpace(pinCode))
            throw new InvalidOperationException("Manager PIN is required to reject a void.");

        var authUserId = currentUser?.PortalUserId ?? (currentUser?.Id != Guid.Empty ? currentUser?.Id.ToString() : null);
        if (!string.IsNullOrEmpty(authUserId))
        {
            var userPin = await _db.PosUserPins.FirstOrDefaultAsync(p => p.AuthUserId == authUserId);
            if (userPin == null || userPin.PinCode != pinCode)
                throw new InvalidOperationException("Invalid Manager PIN.");
        }
        else
        {
            throw new InvalidOperationException("Unable to identify manager to validate PIN.");
        }

        var currentStatus = order.OrderStatus;
        
        var previousStatusRecord = await _db.OrderStatusHistories
            .Where(h => h.OrderId == orderId && h.NewStatus == "Void Requested")
            .OrderByDescending(h => h.CreatedAt)
            .FirstOrDefaultAsync();
            
        string revertStatus = previousStatusRecord?.OldStatus ?? "Completed";
        var requester = await RequesterPortalIdAsync(orderId, "Void Requested");

        order.OrderStatus = revertStatus;
        order.ApprovedBy = await _staff.CurrentStaffIdAsync() ?? rejectedBy; 
        order.RejectionRemarks = $"Void Rejected: {reason}";
        order.UpdatedAt = DateTime.UtcNow;

        var history = await RecordStatusHistoryAsync(order.OrderId, currentStatus, revertStatus, rejectedBy, $"Void rejected: {reason}");
        RaiseOrderAlert(StaffAlertKinds.VoidRejected, order, history, requester.PortalUserId);
        _db.Orders.Update(order);
        await _db.SaveChangesAsync();
        await AuditOrderAsync(order, history, "void_rejected", "rejected the void request for");

        return MapToResponse(order);
    }

    // One event per saved status change; the history row is the occurrence, so a replay keeps its identity.
    private Task AuditOrderAsync(Order order, OrderStatusHistory history, string operation, string verb, bool? partial = null)
    {
        var target = OrderAudit.Target(order.OrderId, order.OrderNumber);
        return _auditLogService.RecordAsync(new AuditEvent
        {
            Module = PosModules.OrderManagement,
            Action = AuditActions.Updated,
            Target = target,
            Describe = who => $"{who} {verb} {target.DisplayName}{OrderAudit.AtBranch(order.Location?.LocationName, order.LocationId)}.",
            OccurredAt = history.CreatedAt,
            OperationId = $"order-history:{history.Id}",
            Metadata = new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["location_id"] = order.LocationId,
                ["previous_status"] = history.OldStatus,
                ["new_status"] = history.NewStatus,
                ["partial"] = partial,
            },
        });
    }

    // Staff bell: the alert is saved with the history row; that row is the occurrence.
    private void RaiseOrderAlert(string kind, Order order, OrderStatusHistory history, string? person, bool partial = false) =>
        _db.RaiseStaffAlert(new StaffAlertIntent
        {
            Kind = kind,
            History = history,
            LocationId = order.LocationId,
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            PersonPortalUserId = string.IsNullOrWhiteSpace(person) ? null : person,
            Partial = partial,
        });

    // The verified Portal ID of the signed-in requester; null outside a Portal session.
    private string? CurrentPortalUserId() => _httpContextAccessor.HttpContext?.GetCurrentUser()?.PortalUserId;

    private sealed record RequestOrigin(string? PortalUserId, bool Partial);

    /// <summary>
    /// Who filed the open request: the Portal ID captured on its alert, else (requests from before
    /// the bell) the saved staff number's Portal ID. Unknown stays unknown, so the decision reaches
    /// no one rather than the whole branch.
    /// </summary>
    private async Task<RequestOrigin> RequesterPortalIdAsync(int orderId, string requestedStatus)
    {
        var request = await _db.OrderStatusHistories.AsNoTracking()
            .Where(h => h.OrderId == orderId && h.NewStatus == requestedStatus)
            .OrderByDescending(h => h.CreatedAt).ThenByDescending(h => h.Id)
            .Select(h => new { h.Id, h.ChangedBy })
            .FirstOrDefaultAsync();
        if (request is null) return new RequestOrigin(null, false);

        var alert = await _db.StaffAlerts.AsNoTracking()
            .Where(a => a.SourceHistoryId == request.Id)
            .Select(a => new { a.RequesterPortalUserId, a.Partial })
            .FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(alert?.RequesterPortalUserId))
            return new RequestOrigin(alert.RequesterPortalUserId, alert.Partial);

        // Staff numbers start at 1000; 0 and 1 are the old "unknown" placeholders.
        string? fromStaffNumber = null;
        if (request.ChangedBy is int staffNumber && staffNumber >= 1000)
            fromStaffNumber = await _db.PosStaffUsers.AsNoTracking()
                .Where(s => s.Id == staffNumber)
                .Select(s => s.AuthUserId)
                .FirstOrDefaultAsync();
        return new RequestOrigin(fromStaffNumber, alert?.Partial ?? false);
    }

    private async Task<OrderStatusHistory> RecordStatusHistoryAsync(int orderId, string oldStatus, string newStatus, int? changedBy, string? remarks = null)
    {
        var history = new OrderStatusHistory
        {
            OrderId = orderId,
            OldStatus = oldStatus,
            NewStatus = newStatus,
            ChangedBy = await _staff.CurrentStaffIdAsync() ?? changedBy,
            Remarks = remarks,
            CreatedAt = DateTime.UtcNow
        };
        await _db.OrderStatusHistories.AddAsync(history);
        return history;
    }

    private void EnsureCurrentUserCanAccessOrderLocation(Order order)
    {
        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        if (user is null || PosRoleAuthorization.IsAdministrator(user))
        {
            return;
        }

        if (!order.LocationId.HasValue || !user.LocationIds.Contains(order.LocationId.Value))
        {
            throw new InvalidOperationException("You are not authorized to access orders outside your assigned location.");
        }
    }

    private static OrderManagementResponseDto MapToResponse(Order order)
    {
        return new OrderManagementResponseDto
        {
            OrderId               = order.OrderId,
            OrderNumber           = order.OrderNumber,
            OrderType             = order.OrderType,
            OrderSource           = order.OrderSource,
            OrderStatus           = order.OrderStatus,
            PaymentMethod         = order.PaymentMethod,
            PaymentStatus         = order.PaymentStatus,
            RefundMethod          = RefundMethods.For(order.PaymentMethod),
            TotalAmount           = order.TotalAmount,
            AmountTendered        = order.AmountTendered,
            ChangeAmount          = order.ChangeAmount,
            CustomerId            = order.CustomerId,
            LocationId            = order.LocationId,
            LocationName          = order.Location?.LocationName,
            DeliveryAddress       = order.DeliveryAddress,
            InstitutionalStreet   = order.InstitutionalStreet,
            InstitutionalCity     = order.InstitutionalCity,
            InstitutionalProvince = order.InstitutionalProvince,
            InstitutionalZipCode  = order.InstitutionalZipCode,
            ContactPerson         = order.ContactPerson,
            IsPreorder            = order.IsPreorder,
            CustomVariationNotes  = order.CustomVariationNotes,
            SeniorPwdId           = order.SeniorPwdId,
            SeniorPwdName         = order.SeniorPwdName,
            SeniorPwdStreet       = order.SeniorPwdStreet,
            SeniorPwdBarangay     = order.SeniorPwdBarangay,
            SeniorPwdCity         = order.SeniorPwdCity,
            SeniorPwdProvince     = order.SeniorPwdProvince,
            SeniorPwdZipCode      = order.SeniorPwdZipCode,
            SubmittedBy           = order.SubmittedBy,
            ApprovedBy            = order.ApprovedBy,
            ApprovedAt            = order.ApprovedAt,
            RejectionRemarks      = order.RejectionRemarks,
            CreatedAt             = order.CreatedAt,
            UpdatedAt             = order.UpdatedAt,
            Payments = order.Payments?.Select(p => new PaymentResponseDto
            {
                PaymentId              = p.PaymentId,
                OrderId                = p.OrderId,
                AmountPaid             = p.AmountPaid,
                PaymentChannel         = p.PaymentChannel,
                GatewayReferenceNumber = p.GatewayReferenceNumber,
                PaymentStatus          = p.PaymentStatus,
                PaidAt                 = p.PaidAt
            }).ToList() ?? new(),
            Items = order.OrderItems?.Select(oi => new OrderItemResponseDto
            {
                ItemId        = oi.ItemId,
                VariationId   = oi.VariationId,
                ProductName   = oi.ProductVariation?.Product?.ProductName ?? string.Empty,
                VariationName = oi.ProductVariation?.VariationName ?? string.Empty,
                Quantity      = oi.Quantity,
                UnitPrice     = oi.UnitPrice,
                Subtotal      = oi.Subtotal
            }).ToList() ?? new(),
            StatusHistory = order.StatusHistory?.Select(sh => new OrderStatusHistoryResponseDto
            {
                Id = sh.Id,
                OrderId = sh.OrderId,
                OldStatus = sh.OldStatus,
                NewStatus = sh.NewStatus,
                ChangedBy = sh.ChangedBy,
                Remarks = sh.Remarks,
                CreatedAt = sh.CreatedAt
            }).OrderBy(sh => sh.CreatedAt).ToList() ?? new()
        };
    }
}
