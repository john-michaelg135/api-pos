using System.Text;

namespace Applications.ProofFiles;

/// <summary>
/// Settings for proof photos kept in the shared file-storage service. Calls go through the API
/// gateway as its "pos" Caller (POS_GATEWAY_URL, POS_GATEWAY_CALLER_KEY, shared with audit
/// delivery). POS_UPLOAD_SIGNING_SECRET signs the upload references handed to the browser.
/// Incomplete settings turn photo uploads off; saved legacy photos stay readable.
/// </summary>
public sealed class ProofStorageOptions
{
    public const string GatewayUrlVariable = "POS_GATEWAY_URL";
    public const string CallerKeyVariable = "POS_GATEWAY_CALLER_KEY";
    public const string SigningSecretVariable = "POS_UPLOAD_SIGNING_SECRET";

    /// <summary>Same budget as the 3,000,000-character JPEG data-URL limit POS accepted before.</summary>
    public const long MaxJpegBytes = 2_249_982;

    public const int MinSigningSecretBytes = 32;

    public string? GatewayUrl { get; init; }
    public string? CallerKey { get; init; }
    public string? SigningSecret { get; init; }

    /// <summary>Deadline for one gateway call.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Deadline for reading an original back from its signed address.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan PendingReferenceLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan ReadyReferenceLifetime { get; init; } = TimeSpan.FromMinutes(60);

    public bool HasGateway => !string.IsNullOrWhiteSpace(GatewayUrl) && !string.IsNullOrWhiteSpace(CallerKey)
        && Uri.TryCreate(GatewayUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public bool HasSigningSecret => SigningSecret is not null && Encoding.UTF8.GetByteCount(SigningSecret) >= MinSigningSecretBytes;

    /// <summary>New uploads need both the gateway and the signing secret.</summary>
    public bool UploadsEnabled => HasGateway && HasSigningSecret;

    public static ProofStorageOptions FromEnvironment(Func<string, string?> read) => new()
    {
        GatewayUrl = read(GatewayUrlVariable)?.Trim().TrimEnd('/'),
        CallerKey = read(CallerKeyVariable)?.Trim(),
        SigningSecret = string.IsNullOrWhiteSpace(read(SigningSecretVariable)) ? null : read(SigningSecretVariable)!.Trim(),
    };
}
