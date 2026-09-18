using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Json;
using Applications.Interfaces;
using Api.Contracts.Inventory;

namespace Applications.Services;

public class ScmsSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ScmsSyncBackgroundService> _logger;
    private readonly HttpClient _httpClient;

    public ScmsSyncBackgroundService(IServiceProvider serviceProvider, ILogger<ScmsSyncBackgroundService> logger, IHttpClientFactory httpClientFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("ScmsClient");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncWithScmsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing with SCMS");
            }
            
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task SyncWithScmsAsync()
    {
        _logger.LogInformation("Starting SCMS Sync...");

        // Fetch from SCMS
        var scmsItems = await _httpClient.GetFromJsonAsync<List<ScmsItemDto>>("/api/Items");
        if (scmsItems == null || scmsItems.Count == 0) return;

        using var scope = _serviceProvider.CreateScope();
        var inventoryService = scope.ServiceProvider.GetRequiredService<IInventoryService>();

        // We assume Location 999 for Commissary POS sync
        int defaultLocationId = 999;

        // Get all current stock to calculate delta
        var posStocks = await inventoryService.GetAllStockAsync();

        foreach (var scmsItem in scmsItems)
        {
            // Map ItemId -> VariationId. We assume 1:1 mapping for this MVP.
            int variationId = scmsItem.ItemId;

            var posStock = posStocks.FirstOrDefault(s => s.VariationId == variationId && s.LocationId == defaultLocationId);
            int currentPosStock = posStock?.Quantity ?? 0;

            // Delta
            int difference = scmsItem.CurrentStock - currentPosStock;

            if (difference > 0)
            {
                // We received stock from SCMS
                var receivingDto = new StockReceivingDto
                {
                    VariationId = variationId,
                    LocationId = defaultLocationId,
                    QuantityReceived = difference,
                    Notes = "SCMS_SYNC",
                    ReceivedBy = 999 // System user ID
                };
                
                await inventoryService.ReceiveStockAsync(receivingDto);
                _logger.LogInformation($"Synced Variation {variationId}: +{difference}");
            }
            // For reductions, maybe we do adjustments, but usually POS deducts from sales. 
            // We ignore negative difference for now since POS might be ahead of SCMS in terms of sales deduplications.
        }

        _logger.LogInformation("SCMS Sync Completed.");
    }
}

public class ScmsItemDto
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SKU { get; set; } = string.Empty;
    public int CurrentStock { get; set; }
}
