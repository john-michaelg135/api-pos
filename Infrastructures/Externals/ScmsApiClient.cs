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

    /// <summary>
    /// Updates the status of a specific stock transfer on the SCMS backend.
    /// PUT {SCMS_API_BASE_URL}/api/StockTransfers/{transferId}/status
    /// </summary>
    public async Task UpdateTransferStatusAsync(int transferId, string status)
    {
        try
        {
            var response = await _http.PutAsJsonAsync($"/api/StockTransfers/{transferId}/status", new { status });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to update transfer status on SCM API: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error calling SCM API to update transfer status: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sends a receive confirmation to the SCMS API when a branch receives a transfer.
    /// </summary>
    public async Task<bool> SendReceiveConfirmationAsync(string transferId, ReceiveConfirmationDto dto)
    {
        try
        {
            var response = await _http.PostAsJsonAsync($"/api/scms/transfers/{transferId}/receive-confirmation", dto);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to send confirmation to SCMS API: {ex.Message}", ex);
        }
    }
}
