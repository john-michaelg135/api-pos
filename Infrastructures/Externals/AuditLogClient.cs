using Api.Contracts.Shared;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Infrastructures.Externals;

public class AuditLogClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AuditLogClient> _logger;

    public AuditLogClient(IHttpClientFactory httpClientFactory, ILogger<AuditLogClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SendAsync(AuditLogEntry entry)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("AuditService");
            var content = new StringContent(JsonSerializer.Serialize(entry), System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/audit-logs", content);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failed to send audit log. Status code: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send audit log to the shared audit service.");
        }
    }
}
