using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Contracts.Shared;

/// <summary>
/// The body of <c>POST {POS_GATEWAY_URL}/audit/events</c>, which the gateway forwards to the
/// audit-logs service's <c>POST /events</c>.
/// </summary>
public sealed class AuditLogEntry
{
    public const string PosSource = "POS";

    /// <summary>Whole body limit and metadata limit, in UTF-8 bytes.</summary>
    public const int MaxBodyBytes = 16 * 1024;
    public const int MaxMetadataBytes = 4096;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonPropertyName("source")] public string Source { get; init; } = PosSource;
    [JsonPropertyName("module")] public string? Module { get; init; }
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("outcome")] public required string Outcome { get; init; }
    [JsonPropertyName("actor")] public required AuditIdentity Actor { get; init; }
    [JsonPropertyName("target")] public required AuditIdentity Target { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    /// <summary>UTC, e.g. 2026-11-19T03:14:00.000Z.</summary>
    [JsonPropertyName("occurred_at")] public required string OccurredAt { get; init; }
    [JsonPropertyName("request_id")] public string? RequestId { get; init; }
    [JsonPropertyName("metadata")] public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
    [JsonPropertyName("idempotency_key")] public required string IdempotencyKey { get; init; }

    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static int MetadataBytes(IReadOnlyDictionary<string, object?> metadata) =>
        JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions).Length;

    public static string FormatTimestamp(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Lowercase SHA-256 of the compact JSON array [action, target.type, target.id, operationId],
    /// as CRMS derives it, so a replay of the same occurrence returns the same event.
    /// </summary>
    public static string IdempotencyKeyFor(string action, string targetType, string targetId, string operationId)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new[] { action, targetType, targetId, operationId }, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }
}

public sealed record AuditIdentity(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName);

/// <summary>What became of one write; the boundary logs anything but <see cref="Delivered"/>.</summary>
public sealed record AuditWriteResult(string Category, int? StatusCode = null, string? EventId = null)
{
    public bool Delivered => Category == "delivered";

    public static AuditWriteResult FromStatus(int status, string? eventId = null) => status switch
    {
        >= 200 and < 300 => new("delivered", status, eventId),
        401 or 403 => new("credential_rejected", status),
        429 => new("throttled", status),
        >= 500 => new("gateway_unavailable", status),
        _ => new("payload_rejected", status),
    };

    public static readonly AuditWriteResult Timeout = new("timeout");
    public static readonly AuditWriteResult NetworkError = new("network_error");
    public static readonly AuditWriteResult NotConfigured = new("not_configured");
}
