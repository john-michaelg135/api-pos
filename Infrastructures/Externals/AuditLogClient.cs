using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Api.Contracts.Shared;
using Applications.Audit;
using Applications.Interfaces;

namespace Infrastructures.Externals;

/// <summary>
/// Gateway adapter: <c>POST {POS_GATEWAY_URL}/audit/events</c> as the "pos" Caller, bounded to five
/// seconds. A successful write, including a replay of the same idempotency key, answers 201 { id }.
/// </summary>
public sealed class AuditLogClient : IAuditWriter
{
    public const string HttpClientName = "AuditGateway";

    private static readonly MediaTypeHeaderValue Json = new("application/json");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuditDeliveryOptions _options;

    public AuditLogClient(IHttpClientFactory httpClientFactory, AuditDeliveryOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<AuditWriteResult> WriteAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_options.IsComplete) return AuditWriteResult.NotConfigured;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);

        var content = new ByteArrayContent(entry.ToUtf8Json());
        content.Headers.ContentType = Json;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.GatewayUrl}/audit/events") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.CallerKey);

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode) return AuditWriteResult.FromStatus((int)response.StatusCode);

            string? id = null;
            try
            {
                id = (await response.Content.ReadFromJsonAsync<Created>(deadline.Token))?.Id;
            }
            catch (System.Text.Json.JsonException)
            {
                // Written all the same; the id is only for the operator log.
            }
            return AuditWriteResult.FromStatus((int)response.StatusCode, id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuditWriteResult.Timeout;
        }
        catch (HttpRequestException)
        {
            return AuditWriteResult.NetworkError;
        }
    }

    private sealed record Created([property: JsonPropertyName("id")] string? Id);
}
