using Api.Contracts.Shared;
using Api.Middlewares;
using Applications.Audit;
using Applications.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Applications.Services;

/// <summary>
/// The POS audit boundary: resolves the verified actor, keeps metadata to the allowlist and size
/// limits, derives the idempotency key and awaits one bounded write. Every failure, including an
/// exception from an injected writer, is logged here and never reaches the business result.
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxSubmissionIdLength = 128;
    private const int MaxDescriptionLength = 1000;

    private readonly IAuditWriter _writer;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuditDeliveryOptions _options;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        IAuditWriter writer,
        IHttpContextAccessor httpContextAccessor,
        AuditDeliveryOptions options,
        ILogger<AuditLogService> logger)
    {
        _writer = writer;
        _httpContextAccessor = httpContextAccessor;
        _options = options;
        _logger = logger;
    }

    public async Task RecordAsync(AuditEvent auditEvent)
    {
        if (!_options.Enabled) return;

        string? requestId = null;
        try
        {
            var http = _httpContextAccessor.HttpContext;
            requestId = http?.TraceIdentifier;

            // Only the Portal session middleware's validated user counts; never re-derive one from headers.
            var actor = auditEvent.Actor
                ?? AuditActor.Staff(http?.Items["CurrentUser"] as CurrentUserContext);
            if (actor is null)
            {
                _logger.LogWarning(
                    "Audit event {Action} on {TargetType}:{TargetId} skipped: no verified staff actor (request {RequestId}).",
                    auditEvent.Action, auditEvent.Target.Type, auditEvent.Target.Id, requestId);
                return;
            }

            var operationId = auditEvent.OperationId ?? $"submission:{SubmissionId(http)}";
            var entry = new AuditLogEntry
            {
                Module = auditEvent.Module,
                Action = auditEvent.Action,
                Outcome = auditEvent.Outcome,
                Actor = new AuditIdentity(actor.Type, actor.Id, actor.DisplayName),
                Target = new AuditIdentity(auditEvent.Target.Type, auditEvent.Target.Id, auditEvent.Target.DisplayName),
                Description = Truncate(auditEvent.Describe(actor.DisplayName), MaxDescriptionLength),
                OccurredAt = AuditLogEntry.FormatTimestamp(auditEvent.OccurredAt),
                RequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId,
                Metadata = AllowedMetadata(auditEvent),
                IdempotencyKey = AuditLogEntry.IdempotencyKeyFor(
                    auditEvent.Action, auditEvent.Target.Type, auditEvent.Target.Id, operationId),
            };

            if (entry.ToUtf8Json().Length > AuditLogEntry.MaxBodyBytes)
            {
                Report(auditEvent, new AuditWriteResult("payload_too_large"), requestId);
                return;
            }

            var result = await _writer.WriteAsync(entry);
            if (result.Delivered)
            {
                _logger.LogDebug("Audit event {EventId} recorded for {Action} on {TargetType}:{TargetId}.",
                    result.EventId, auditEvent.Action, auditEvent.Target.Type, auditEvent.Target.Id);
            }
            else
            {
                Report(auditEvent, result, requestId);
            }
        }
        catch (Exception exception)
        {
            Report(auditEvent, new AuditWriteResult($"writer_error:{exception.GetType().Name}"), requestId);
        }
    }

    // Status or failure category, action, target and correlation only: no credentials, no payload.
    private void Report(AuditEvent auditEvent, AuditWriteResult result, string? requestId) =>
        _logger.LogWarning(
            "Audit event {Action} on {TargetType}:{TargetId} not delivered: {Category} (HTTP {StatusCode}, request {RequestId}).",
            auditEvent.Action, auditEvent.Target.Type, auditEvent.Target.Id,
            result.Category, result.StatusCode?.ToString() ?? "-", requestId ?? "-");

    private IReadOnlyDictionary<string, object?>? AllowedMetadata(AuditEvent auditEvent)
    {
        var metadata = auditEvent.Metadata
            .Where(pair => AuditMetadata.AllowedKeys.Contains(pair.Key) && pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var dropped = auditEvent.Metadata.Keys.Where(key => !AuditMetadata.AllowedKeys.Contains(key)).ToArray();
        if (dropped.Length > 0)
        {
            _logger.LogWarning("Audit event {Action} on {TargetType}:{TargetId}: dropped metadata keys outside the allowlist: {Keys}.",
                auditEvent.Action, auditEvent.Target.Type, auditEvent.Target.Id, string.Join(", ", dropped));
        }

        if (metadata.Count > 0 && AuditLogEntry.MetadataBytes(metadata) > AuditLogEntry.MaxMetadataBytes)
        {
            _logger.LogWarning("Audit event {Action} on {TargetType}:{TargetId}: metadata over {Limit} bytes; kept the operation only.",
                auditEvent.Action, auditEvent.Target.Type, auditEvent.Target.Id, AuditLogEntry.MaxMetadataBytes);
            metadata = metadata.Where(pair => pair.Key == "operation").ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        return metadata.Count == 0 ? null : metadata;
    }

    // The same edit retried by the browser carries the same Idempotency-Key; each new edit gets its own id.
    private static string SubmissionId(HttpContext? http)
    {
        var header = http?.Request.Headers[IdempotencyHeader].ToString().Trim();
        return !string.IsNullOrEmpty(header) && header.Length <= MaxSubmissionIdLength && header.All(IsSafe)
            ? header
            : Guid.NewGuid().ToString("N");

        static bool IsSafe(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':';
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
