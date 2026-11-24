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
    /// Transfer ids are strings end-to-end (e.g. "trans_001"); SCM adheres to this contract.
    /// </summary>
    public async Task UpdateTransferStatusAsync(string transferId, string status)
    {
        try
        {
            using var content = await BufferedJsonAsync(new { status });
            var response = await _http.PutAsync($"/api/StockTransfers/{transferId}/status", content);
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
    /// Sends POS's whole branch list to SCMS (POST /api/pos/locations/sync). Returns null on
    /// success, or a short reason it failed; never throws, so a branch save can't fail on it.
    /// </summary>
    public async Task<string?> SyncLocationsAsync(ScmsLocationSyncRequest body, CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = await BufferedJsonAsync(body);
            using var response = await _http.PostAsync("/api/pos/locations/sync", content, cancellationToken);
            if (response.IsSuccessStatusCode) return null;

            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            if (detail.Length > 200) detail = detail[..200];
            return $"SCMS answered HTTP {(int)response.StatusCode}{(string.IsNullOrWhiteSpace(detail) ? "" : $": {detail}")}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"SCMS could not be reached ({ex.GetType().Name}).";
        }
    }

    /// <summary>
    /// Sends a branch's stock request to IMS (POST /api/pos/stock-requests). Never throws:
    /// 200/201 → Accepted (with IMS's id), 400/404/409/422 → Refused with IMS's message,
    /// anything else (5xx, 401/403, timeout, network) → Unreachable, so the sweep retries.
    /// </summary>
    public async Task<StockRequestSubmitResult> SubmitStockRequestAsync(StockRequestDto body, CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = await BufferedJsonAsync(body);
            using var response = await _http.PostAsync("/api/pos/stock-requests", content, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
                return new(StockRequestSubmitOutcome.Accepted, ReadString(text, "data", "imsRequestId"), null);

            var status = (int)response.StatusCode;
            var message = ReadString(text, "message");
            if (status is 400 or 404 or 409 or 422)
            {
                message = string.IsNullOrWhiteSpace(message) ? $"IMS refused the request (HTTP {status})." : message.Trim();
                return new(StockRequestSubmitOutcome.Refused, null, Truncate(message, 500));
            }

            // 401/403 is a key problem on our side, not a verdict on the request: keep retrying.
            var detail = string.IsNullOrWhiteSpace(message) ? text : message;
            return new(StockRequestSubmitOutcome.Unreachable, null,
                Truncate($"IMS answered HTTP {status}{(string.IsNullOrWhiteSpace(detail) ? "" : $": {detail.Trim()}")}", 300));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(StockRequestSubmitOutcome.Unreachable, null, $"IMS could not be reached ({ex.GetType().Name}).");
        }
    }

    private static string? ReadString(string json, params string[] path)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var node = doc.RootElement;
            foreach (var key in path)
            {
                if (node.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                var found = false;
                foreach (var property in node.EnumerateObject())
                {
                    if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                    node = property.Value;
                    found = true;
                    break;
                }
                if (!found) return null;
            }
            return node.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => node.GetString(),
                System.Text.Json.JsonValueKind.Number => node.GetRawText(),
                _ => null,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Sends a receive confirmation to the SCMS API when a branch receives a transfer.
    /// </summary>
    public async Task<bool> SendReceiveConfirmationAsync(string transferId, ReceiveConfirmationDto dto)
    {
        try
        {
            using var content = await BufferedJsonAsync(dto);
            var response = await _http.PostAsync($"/api/scms/transfers/{transferId}/receive-confirmation", content);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to send confirmation to SCMS API: {ex.Message}", ex);
        }
    }

    // Buffered so the request carries a Content-Length; the *AsJsonAsync helpers stream it
    // chunked, which some servers read as an empty body.
    private static async Task<JsonContent> BufferedJsonAsync<T>(T body)
    {
        var content = JsonContent.Create(body);
        await content.LoadIntoBufferAsync();
        return content;
    }
}
