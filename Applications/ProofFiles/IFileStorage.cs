using System.Text.Json.Serialization;

namespace Applications.ProofFiles;

/// <summary>
/// The shared file-storage service, reached through the API gateway. Only JSON metadata crosses
/// the gateway; image bytes move between the browser (or POS, for validation) and storage-signed
/// addresses.
/// </summary>
public interface IFileStorage
{
    /// <summary>POST /files: a Pending File and a signed upload address.</summary>
    Task<UploadAddress> RequestUploadAsync(UploadRequest request, CancellationToken cancellationToken = default);

    /// <summary>POST /files/{id}/confirm. 409 = nothing uploaded; 422 = bytes don't match the declaration (storage removed them).</summary>
    Task<StoredFile> ConfirmAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>GET /files/{id}: an Active File with a short-lived view address.</summary>
    Task<ViewedFile> ViewAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>PATCH /files/{id} {"retentionType": ...}.</summary>
    Task<StoredFile> SetRetentionAsync(Guid fileId, string retentionType, CancellationToken cancellationToken = default);

    /// <summary>POST /files/{id}/links. Repeating an existing Link succeeds.</summary>
    Task AddLinkAsync(Guid fileId, FileLink link, CancellationToken cancellationToken = default);

    /// <summary>DELETE /files/{id}/links with the exact Link. Removing an absent Link succeeds.</summary>
    Task RemoveLinkAsync(Guid fileId, FileLink link, CancellationToken cancellationToken = default);

    /// <summary>DELETE /files/{id}: removes the File for every Link. Only for unattached rejected uploads.</summary>
    Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> + 1 bytes from a storage-signed HTTPS address, without
    /// POS credentials. Returns what was read; a longer body comes back one byte over the limit.
    /// </summary>
    Task<byte[]> ReadOriginalAsync(Uri signedUrl, long maxBytes, CancellationToken cancellationToken = default);
}

/// <summary>Storage refused the call (Status set) or could not be reached in time (Status null).</summary>
public sealed class FileStorageException : Exception
{
    public int? Status { get; }

    public FileStorageException(int? status, string message, Exception? inner = null) : base(message, inner) => Status = status;

    public bool IsUnavailable => Status is null or >= 500 or 401 or 403 or 429;
}

public sealed record UploadRequest(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("retentionType")] string RetentionType);

public record StoredFile
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("filename")] public string Filename { get; init; } = "";
    [JsonPropertyName("contentType")] public string ContentType { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("visibility")] public string Visibility { get; init; } = "";
    [JsonPropertyName("retentionType")] public string RetentionType { get; init; } = "";
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record UploadAddress : StoredFile
{
    [JsonPropertyName("uploadUrl")] public string UploadUrl { get; init; } = "";
    [JsonPropertyName("uploadUrlExpiresInSeconds")] public int UploadUrlExpiresInSeconds { get; init; }
}

public sealed record ViewedFile : StoredFile
{
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

public static class FileStorageValues
{
    public const string Private = "private";
    public const string Temporary = "temporary";
    public const string Permanent = "permanent";
    public const string Active = "active";
    public const string Jpeg = "image/jpeg";

    /// <summary>Only HTTPS storage addresses without embedded credentials are handed out or read.</summary>
    public static Uri? SafeStorageUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;
}
