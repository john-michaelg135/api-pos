using Api.Contracts.OrderManagement;
using Api.Middlewares;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Applications.ProofFiles;

/// <summary>A refusal with the HTTP status the controller should answer with.</summary>
public sealed class ProofUploadException : Exception
{
    public int Status { get; }
    public bool CleanupFailed { get; }

    public ProofUploadException(int status, string message, bool cleanupFailed = false) : base(message)
    {
        Status = status;
        CleanupFailed = cleanupFailed;
    }
}

public interface IProofUploadService
{
    /// <summary>Step 1: a signed upload address and a pending reference for this order and purpose.</summary>
    Task<ProofUploadStartedDto> StartAsync(int orderId, string kind, ProofUploadRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>Step 3: confirm with storage, validate the stored bytes and issue a ready reference.</summary>
    Task<ProofUploadReadyDto> ConfirmAsync(int orderId, string kind, string? pendingReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Step 4 (inside the business save): the ready reference this user issued for this order and
    /// purpose, or an <see cref="InvalidOperationException"/> the order screen can show.
    /// </summary>
    ProofReference OpenReadyReference(int orderId, string kind, string? proofReference);
}

/// <summary>
/// POS side of the direct-upload flow (same shape as CRMS): POS decides the policy, the browser PUTs
/// bytes straight to storage, and POS checks what landed before vouching for it with an HMAC-signed
/// reference bound to File, uploader, order, purpose, declared metadata, phase and expiry.
/// </summary>
public sealed class ProofUploadService : IProofUploadService
{
    private const string Invalid = "That photo upload is invalid or expired. Attach the photo again.";
    private const string Unavailable = "Photo storage is temporarily unavailable. Please try again.";
    private const string Mismatch = "The uploaded photo does not match its declared size or type. Attach it again.";

    /// <summary>A ready reference must leave storage this long to attach the File before it expires.</summary>
    private static readonly TimeSpan MinFileLifetimeAtSave = TimeSpan.FromHours(1);

    private readonly PosDbContext _db;
    private readonly IFileStorage _storage;
    private readonly ProofStorageOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ProofUploadService> _logger;
    private readonly TimeProvider _clock;
    private readonly ProofReferenceCodec? _codec;

    public ProofUploadService(PosDbContext db, IFileStorage storage, ProofStorageOptions options,
        IHttpContextAccessor httpContextAccessor, ILogger<ProofUploadService> logger, TimeProvider? clock = null)
    {
        _db = db;
        _storage = storage;
        _options = options;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _codec = options.HasSigningSecret ? new ProofReferenceCodec(options.SigningSecret!) : null;
    }

    public async Task<ProofUploadStartedDto> StartAsync(int orderId, string kind, ProofUploadRequestDto request, CancellationToken cancellationToken = default)
    {
        var (codec, staffId) = await AuthorizeAsync(orderId, kind, cancellationToken);

        var contentType = request?.ContentType?.Trim().ToLowerInvariant();
        if (contentType != FileStorageValues.Jpeg)
            throw new ProofUploadException(StatusCodes.Status400BadRequest, "Proof photos must be JPEG images.");
        if (request!.Size <= 0)
            throw new ProofUploadException(StatusCodes.Status400BadRequest, "The photo is empty.");
        if (request.Size > ProofStorageOptions.MaxJpegBytes)
            throw new ProofUploadException(StatusCodes.Status400BadRequest, "Proof photo is too large.");
        var filename = SafeFilename(request.Filename, kind);

        // Check the signing key works before creating anything remotely.
        var now = _clock.GetUtcNow();
        UploadAddress pending;
        try
        {
            pending = await _storage.RequestUploadAsync(new UploadRequest(filename, FileStorageValues.Jpeg, request.Size,
                FileStorageValues.Private, FileStorageValues.Temporary), cancellationToken);
        }
        catch (FileStorageException ex)
        {
            _logger.LogWarning("Proof upload request for order {OrderId} ({Kind}) failed: storage status {Status}.", orderId, kind, ex.Status?.ToString() ?? "unreachable");
            throw new ProofUploadException(StatusCodes.Status503ServiceUnavailable, Unavailable);
        }

        var uploadUrl = FileStorageValues.SafeStorageUrl(pending.UploadUrl);
        if (pending.Id == Guid.Empty || uploadUrl is null)
        {
            _logger.LogWarning("Proof upload request for order {OrderId} ({Kind}): storage returned no usable HTTPS upload address.", orderId, kind);
            throw new ProofUploadException(StatusCodes.Status503ServiceUnavailable, Unavailable);
        }

        var reference = codec.Seal(new ProofReference
        {
            FileId = pending.Id,
            StaffId = staffId,
            OrderId = orderId,
            Kind = kind,
            Filename = filename,
            ContentType = FileStorageValues.Jpeg,
            Size = request.Size,
            Phase = ProofReferencePhases.Pending,
            ExpiresAt = now.Add(_options.PendingReferenceLifetime).ToUnixTimeSeconds(),
        });

        return new ProofUploadStartedDto
        {
            UploadUrl = uploadUrl.ToString(),
            UploadUrlExpiresInSeconds = pending.UploadUrlExpiresInSeconds,
            PendingReference = reference,
        };
    }

    public async Task<ProofUploadReadyDto> ConfirmAsync(int orderId, string kind, string? pendingReference, CancellationToken cancellationToken = default)
    {
        var (codec, staffId) = await AuthorizeAsync(orderId, kind, cancellationToken);
        var now = _clock.GetUtcNow();

        var upload = codec.Open(pendingReference);
        if (upload is null || upload.Phase != ProofReferencePhases.Pending || upload.StaffId != staffId ||
            upload.OrderId != orderId || upload.Kind != kind || upload.IsExpired(now))
            throw new ProofUploadException(StatusCodes.Status400BadRequest, Invalid);

        StoredFile confirmed;
        try
        {
            confirmed = await _storage.ConfirmAsync(upload.FileId, cancellationToken);
        }
        catch (FileStorageException ex) when (ex.Status == StatusCodes.Status409Conflict)
        {
            // Nothing uploaded yet, or already confirmed by an earlier attempt whose answer was lost.
            confirmed = await AlreadyConfirmedAsync(upload, cancellationToken)
                ?? throw new ProofUploadException(StatusCodes.Status409Conflict, "The photo hasn't finished uploading. Try again, or attach it again.");
        }
        catch (FileStorageException ex) when (ex.Status == StatusCodes.Status422UnprocessableEntity)
        {
            // Storage already removed the mismatching object.
            throw new ProofUploadException(StatusCodes.Status422UnprocessableEntity, Mismatch);
        }
        catch (FileStorageException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            throw new ProofUploadException(StatusCodes.Status400BadRequest, Invalid);
        }
        catch (FileStorageException ex)
        {
            _logger.LogWarning("Proof confirmation for order {OrderId} ({Kind}) failed: storage status {Status}.", orderId, kind, ex.Status?.ToString() ?? "unreachable");
            throw new ProofUploadException(StatusCodes.Status503ServiceUnavailable, Unavailable);
        }

        var metadataMatches = confirmed.Id == upload.FileId &&
            confirmed.Status == FileStorageValues.Active &&
            confirmed.Visibility == FileStorageValues.Private &&
            confirmed.RetentionType == FileStorageValues.Temporary &&
            confirmed.ExpiresAt is { } expiresAt && expiresAt > now &&
            string.Equals(confirmed.ContentType, upload.ContentType, StringComparison.OrdinalIgnoreCase) &&
            confirmed.Size == upload.Size;
        if (!metadataMatches)
            await RejectAsync(upload, "metadata_mismatch", Mismatch, cancellationToken);

        // Read the original back and check the real bytes, not just what storage was told.
        byte[] bytes;
        try
        {
            var viewed = await _storage.ViewAsync(upload.FileId, cancellationToken);
            var url = FileStorageValues.SafeStorageUrl(viewed.Url);
            if (viewed.Id != upload.FileId || url is null)
                throw new FileStorageException(null, "Storage returned no usable HTTPS view address.");
            bytes = await _storage.ReadOriginalAsync(url, upload.Size, cancellationToken);
        }
        catch (FileStorageException ex)
        {
            _logger.LogWarning("Proof validation read for order {OrderId} ({Kind}) failed: storage status {Status}.", orderId, kind, ex.Status?.ToString() ?? "unreachable");
            throw new ProofUploadException(StatusCodes.Status503ServiceUnavailable, Unavailable);
        }

        if (bytes.LongLength != upload.Size || !LooksLikeJpeg(bytes))
            await RejectAsync(upload, "content_mismatch", Mismatch, cancellationToken);

        var ready = upload with
        {
            Phase = ProofReferencePhases.Ready,
            ExpiresAt = now.Add(_options.ReadyReferenceLifetime).ToUnixTimeSeconds(),
            FileExpiresAt = confirmed.ExpiresAt!.Value.ToUnixTimeSeconds(),
        };
        return new ProofUploadReadyDto
        {
            ProofReference = codec.Seal(ready),
            ProofReferenceExpiresInSeconds = (int)_options.ReadyReferenceLifetime.TotalSeconds,
            Filename = upload.Filename,
            ContentType = upload.ContentType,
            Size = upload.Size,
        };
    }

    public ProofReference OpenReadyReference(int orderId, string kind, string? proofReference)
    {
        if (_codec is null)
            throw new InvalidOperationException("Photo uploads are not available right now. Attach the photo again later.");

        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        var staffId = StaffIdOf(user);
        var now = _clock.GetUtcNow();
        var reference = _codec.Open(proofReference);
        if (reference is null || staffId is null || reference.Phase != ProofReferencePhases.Ready || reference.StaffId != staffId ||
            reference.OrderId != orderId || reference.Kind != kind || reference.IsExpired(now) ||
            reference.ContentType != FileStorageValues.Jpeg || reference.Size <= 0 || reference.Size > ProofStorageOptions.MaxJpegBytes ||
            reference.FileExpiresAt is not { } fileExpires ||
            DateTimeOffset.FromUnixTimeSeconds(fileExpires) <= now.Add(MinFileLifetimeAtSave))
            throw new InvalidOperationException(Invalid);
        return reference;
    }

    /// <summary>User, Order Management role for this purpose, the order and its branch, at every step.</summary>
    private async Task<(ProofReferenceCodec Codec, string StaffId)> AuthorizeAsync(int orderId, string kind, CancellationToken cancellationToken)
    {
        if (!ProofKinds.IsKnown(kind))
            throw new ProofUploadException(StatusCodes.Status404NotFound, "Unknown proof type.");

        var user = _httpContextAccessor.HttpContext?.GetCurrentUser();
        var staffId = StaffIdOf(user);
        if (user is null || staffId is null)
            throw new ProofUploadException(StatusCodes.Status401Unauthorized, "A valid POS session is required.");

        var allowed = kind == ProofKinds.Delivery ? PosRoleAuthorization.IsManager(user) : PosRoleAuthorization.IsCashierOrManager(user);
        if (!allowed)
            throw new ProofUploadException(StatusCodes.Status403Forbidden, kind == ProofKinds.Delivery
                ? "Only Order Managers and Admins can confirm deliveries."
                : "Your POS role may not request refunds.");

        if (_codec is null || !_options.HasGateway)
            throw new ProofUploadException(StatusCodes.Status503ServiceUnavailable, "Photo uploads are not configured.");

        var order = await _db.Orders.AsNoTracking()
            .Select(o => new { o.OrderId, o.LocationId, o.OrderSource, o.OrderType, o.OrderStatus })
            .FirstOrDefaultAsync(o => o.OrderId == orderId, cancellationToken)
            ?? throw new ProofUploadException(StatusCodes.Status404NotFound, $"Order with ID {orderId} not found.");

        if (!PosRoleAuthorization.IsAdministrator(user) &&
            (!order.LocationId.HasValue || !user.LocationIds.Contains(order.LocationId.Value)))
            throw new ProofUploadException(StatusCodes.Status403Forbidden, "You are not authorized to access orders outside your assigned location.");

        // Same cashier rule the refund request applies.
        if (kind == ProofKinds.Refund && user.SubRole == "Cashier" && user.LocationId.HasValue &&
            order.LocationId != user.LocationId && order.OrderSource != "Ecommerce")
            throw new ProofUploadException(StatusCodes.Status403Forbidden, "You are not authorized to perform this action for other locations or ecommerce orders.");

        // Early, cheap eligibility checks; the save rechecks everything.
        if (kind == ProofKinds.Delivery &&
            (order.OrderType != "Institutional" || order.OrderStatus != "Shipped" ||
             await _db.OrderDeliveryConfirmations.AnyAsync(c => c.OrderId == orderId, cancellationToken)))
            throw new ProofUploadException(StatusCodes.Status409Conflict, "This order isn't waiting for a delivery confirmation.");
        if (kind == ProofKinds.Refund && order.OrderStatus == "Refund Requested")
            throw new ProofUploadException(StatusCodes.Status409Conflict, "This order already has a refund request waiting for a manager.");

        return (_codec, staffId);
    }

    private async Task<StoredFile?> AlreadyConfirmedAsync(ProofReference upload, CancellationToken cancellationToken)
    {
        try
        {
            var viewed = await _storage.ViewAsync(upload.FileId, cancellationToken);
            return viewed.Status == FileStorageValues.Active ? viewed : null;
        }
        catch (FileStorageException)
        {
            return null;
        }
    }

    /// <summary>Discards the rejected, still-unattached upload and refuses. A failed removal is reported.</summary>
    private async Task RejectAsync(ProofReference upload, string why, string message, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Rejected proof upload {FileId} for order {OrderId} ({Kind}): {Why}.", upload.FileId, upload.OrderId, upload.Kind, why);
        try
        {
            await _storage.DeleteAsync(upload.FileId, cancellationToken);
        }
        catch (FileStorageException ex)
        {
            _logger.LogError("Could not remove rejected proof upload {FileId}: storage status {Status}. It stays temporary and expires on its own.",
                upload.FileId, ex.Status?.ToString() ?? "unreachable");
            throw new ProofUploadException(StatusCodes.Status422UnprocessableEntity,
                $"{message} The rejected upload could not be removed yet; it will expire on its own.", cleanupFailed: true);
        }
        throw new ProofUploadException(StatusCodes.Status422UnprocessableEntity, message);
    }

    /// <summary>JPEG start-of-image marker followed by another marker.</summary>
    internal static bool LooksLikeJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    internal static string? StaffIdOf(CurrentUserContext? user) =>
        user is null ? null
        : !string.IsNullOrWhiteSpace(user.PortalUserId) ? user.PortalUserId
        : user.Id != Guid.Empty ? user.Id.ToString() : null;

    private static string SafeFilename(string? requested, string kind)
    {
        var name = Path.GetFileName(requested ?? string.Empty).Trim();
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/')).ToArray());
        if (name.Length == 0 || name.Length > 120 ||
            !(name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
            return ProofKinds.DefaultFilename(kind);
        return name;
    }
}
