using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Applications.ProofFiles;

public static class ProofReferencePhases
{
    /// <summary>Issued with the signed upload address; only good for confirmation.</summary>
    public const string Pending = "pending";
    /// <summary>Issued after POS validated the stored bytes; only good for the business save.</summary>
    public const string Ready = "ready";
}

/// <summary>
/// What a signed upload reference vouches for. Everything here is decided on the server;
/// the browser only carries the sealed string back.
/// </summary>
public sealed record ProofReference
{
    [JsonPropertyName("f")] public required Guid FileId { get; init; }
    [JsonPropertyName("s")] public required string StaffId { get; init; }
    [JsonPropertyName("o")] public required int OrderId { get; init; }
    [JsonPropertyName("k")] public required string Kind { get; init; }
    [JsonPropertyName("n")] public required string Filename { get; init; }
    [JsonPropertyName("t")] public required string ContentType { get; init; }
    [JsonPropertyName("z")] public required long Size { get; init; }
    [JsonPropertyName("p")] public required string Phase { get; init; }
    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("e")] public required long ExpiresAt { get; init; }
    /// <summary>When file storage expires the still-temporary File (Unix seconds); set once ready.</summary>
    [JsonPropertyName("x")] public long? FileExpiresAt { get; init; }

    public bool IsExpired(DateTimeOffset now) => now.ToUnixTimeSeconds() >= ExpiresAt;
}

/// <summary>
/// HMAC-SHA256 sealed references: <c>v1.&lt;base64url payload&gt;.&lt;base64url mac&gt;</c>.
/// Tampered, foreign or malformed strings open as null.
/// </summary>
public sealed class ProofReferenceCodec
{
    private const string Version = "v1";
    private const int MaxLength = 2048;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly byte[] _key;

    public ProofReferenceCodec(string signingSecret)
    {
        if (Encoding.UTF8.GetByteCount(signingSecret) < ProofStorageOptions.MinSigningSecretBytes)
            throw new ArgumentException($"{ProofStorageOptions.SigningSecretVariable} must be at least {ProofStorageOptions.MinSigningSecretBytes} bytes.");
        // Domain-separate the key so the same secret could never sign something else by accident.
        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes("pos-proof-reference"));
    }

    public string Seal(ProofReference reference)
    {
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(reference, Json));
        return $"{Version}.{payload}.{Base64Url(Mac(payload))}";
    }

    public ProofReference? Open(string? sealedReference)
    {
        if (string.IsNullOrWhiteSpace(sealedReference) || sealedReference.Length > MaxLength) return null;
        var parts = sealedReference.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Version) return null;

        byte[] mac;
        byte[] payload;
        try
        {
            mac = FromBase64Url(parts[2]);
            payload = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(mac, Mac(parts[1]))) return null;

        try
        {
            var reference = JsonSerializer.Deserialize<ProofReference>(payload, Json);
            return reference is { FileId: var id, StaffId.Length: > 0 } && id != Guid.Empty ? reference : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private byte[] Mac(string payload) =>
        HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{Version}.{payload}"));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(s);
    }
}
