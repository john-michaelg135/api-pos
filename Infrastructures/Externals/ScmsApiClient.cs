using System.Net.Http.Json;
using Api.Contracts.Scms;

namespace Infrastructures.Externals;

/// <summary>
/// HTTP client wrapper for the SCMS group's external API.
/// Base URL is configured via the SCMS_API_BASE_URL environment variable.
/// </summary>
public class ScmsApiClient
{
    private readonly HttpClient _http;

    public ScmsApiClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Pulls bulk finished-goods delivery records from the SCMS API.
    /// US-POS-028: Endpoint is GET {SCMS_API_BASE_URL}/api-scm/deliveries
    /// </summary>
    public async Task<ScmsDeliveryPayload> PullBulkDeliveriesAsync()
    {
        try
        {
            var payload = await _http.GetFromJsonAsync<ScmsDeliveryPayload>("/api-scm/deliveries");
            return payload ?? new ScmsDeliveryPayload();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to reach the SCMS API: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error deserializing SCMS delivery payload: {ex.Message}", ex);
        }
    }
}
