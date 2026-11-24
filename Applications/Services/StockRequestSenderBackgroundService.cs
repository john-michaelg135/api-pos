namespace Applications.Services;

/// <summary>
/// Every 5 minutes, re-sends stock requests IMS hasn't acknowledged yet (still Sending), so an IMS
/// outage at submit time heals by itself. After <see cref="StockRequestService.MaxSendAttempts"/>
/// attempts a request stays Sending with its last error, and the branch sees "Couldn't reach IMS".
/// </summary>
public class StockRequestSenderBackgroundService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StockRequestSenderBackgroundService> _logger;

    public StockRequestSenderBackgroundService(IServiceProvider serviceProvider, ILogger<StockRequestSenderBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var requests = scope.ServiceProvider.GetRequiredService<StockRequestService>();
                var delivered = await requests.RetryPendingAsync(stoppingToken);
                if (delivered > 0)
                    _logger.LogInformation("Stock request sweep delivered {Count} request(s) to IMS.", delivered);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Stock request sweep failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (TaskCanceledException) { break; }
        }
    }
}
