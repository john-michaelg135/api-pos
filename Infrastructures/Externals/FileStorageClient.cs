using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Applications.ProofFiles;

namespace Infrastructures.Externals;

/// <summary>
/// Gateway adapter for the shared file-storage service: <c>{POS_GATEWAY_URL}/files...</c> as the
/// "pos" Caller. The gateway adds the storage credential and the POS system code upstream.
/// Signed addresses and the caller key are never logged here.
/// </summary>
public sealed class FileStorageClient : IFileStorage
{
    /// <summary>Named client for gateway calls (bounded by <see cref="ProofStorageOptions.Timeout"/>).</summary>
    public const string GatewayClientName = "FileStorageGateway";

    /// <summary>Named client for signed-address reads: no default headers, no redirects.</summary>
    public const string SignedReadClientName = "FileStorageSignedRead";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProofStorageOptions _options;

    public FileStorageClient(IHttpClientFactory httpClientFactory, ProofStorageOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public Task<UploadAddress> RequestUploadAsync(UploadRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<UploadAddress>(HttpMethod.Post, "", request, cancellationToken);

    public Task<StoredFile> ConfirmAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        SendAsync<StoredFile>(HttpMethod.Post, $"/{fileId:D}/confirm", null, cancellationToken);

    public Task<ViewedFile> ViewAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        SendAsync<ViewedFile>(HttpMethod.Get, $"/{fileId:D}", null, cancellationToken);

    public Task<StoredFile> SetRetentionAsync(Guid fileId, string retentionType, CancellationToken cancellationToken = default) =>
        SendAsync<StoredFile>(HttpMethod.Patch, $"/{fileId:D}", new { retentionType }, cancellationToken);

    public Task AddLinkAsync(Guid fileId, FileLink link, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Post, $"/{fileId:D}/links", LinkBody(link), cancellationToken);

    public Task RemoveLinkAsync(Guid fileId, FileLink link, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"/{fileId:D}/links", LinkBody(link), cancellationToken);

    public Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"/{fileId:D}", null, cancellationToken);

    public async Task<byte[]> ReadOriginalAsync(Uri signedUrl, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (FileStorageValues.SafeStorageUrl(signedUrl.ToString()) is null)
            throw new FileStorageException(null, "Storage returned an address that is not HTTPS.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ReadTimeout);
        try
        {
            var client = _httpClientFactory.CreateClient(SignedReadClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, signedUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new FileStorageException((int)response.StatusCode, "Storage refused to return the original.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var limit = checked((int)maxBytes + 1);
            var buffer = new byte[Math.Min(limit, 256 * 1024)];
            using var collected = new MemoryStream();
            while (collected.Length < limit)
            {
                var want = (int)Math.Min(buffer.Length, limit - collected.Length);
                var read = await stream.ReadAsync(buffer.AsMemory(0, want), deadline.Token);
                if (read == 0) break;
                collected.Write(buffer, 0, read);
            }
            return collected.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FileStorageException(null, "Reading the original from storage timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new FileStorageException(null, "Storage could not be reached to read the original.", ex);
        }
    }

    private static object LinkBody(FileLink link) =>
        new { system = link.System, entityType = link.EntityType, entityId = link.EntityId, role = link.Role };

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (!_options.HasGateway) throw new FileStorageException(null, "File storage is not configured.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);

        using var request = new HttpRequestMessage(method, $"{_options.GatewayUrl}/files{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.CallerKey);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        try
        {
            var client = _httpClientFactory.CreateClient(GatewayClientName);
            using var response = await client.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new FileStorageException((int)response.StatusCode, $"File storage answered {(int)response.StatusCode}.");
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent || typeof(T) == typeof(object))
                return default!;

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(Json, deadline.Token)
                    ?? throw new FileStorageException(null, "File storage returned an empty body.");
            }
            catch (JsonException ex)
            {
                throw new FileStorageException(null, "File storage returned an unreadable body.", ex);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FileStorageException(null, "File storage timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new FileStorageException(null, "File storage could not be reached.", ex);
        }
    }
}
