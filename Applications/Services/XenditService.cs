using System.Text.Json;
using System.Text;
using Applications.Interfaces;
using Microsoft.Extensions.Logging;

namespace Applications.Services;

public class XenditService : IXenditService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<XenditService> _logger;

    public XenditService(IHttpClientFactory httpClientFactory, ILogger<XenditService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> CreateInvoiceAsync(string orderNumber, decimal amount, string description)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("XenditClient");
            var redirectUrl = Environment.GetEnvironmentVariable("XENDIT_REDIRECT_URL") ?? "http://localhost:3003/sales-processing";

            var payload = new
            {
                external_id = orderNumber,
                amount = amount,
                description = description,
                invoice_duration = 86400, // 24 hours
                success_redirect_url = redirectUrl,
                failure_redirect_url = redirectUrl
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("v2/invoices", content);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("Xendit Invoice creation failed. Status: {Status}, Error: {Error}", response.StatusCode, errorContent);
                throw new Exception($"Xendit API returned error: {response.StatusCode} - {errorContent}");
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("invoice_url", out var urlElement))
            {
                return urlElement.GetString() ?? string.Empty;
            }

            throw new Exception("Invoice URL was not returned in Xendit response.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Xendit invoice for order {OrderNumber}", orderNumber);
            throw;
        }
    }
}
