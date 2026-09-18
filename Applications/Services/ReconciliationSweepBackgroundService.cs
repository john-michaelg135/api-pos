using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Applications.Interfaces;

namespace Applications.Services;

/// <summary>
/// Periodic POS-side reconciliation sweep. Re-attempts SCM status callbacks for
/// transfers that were received but not yet acknowledged (Pending/Failed), so a
/// transient SCM outage during receipt self-heals without manual intervention.
/// </summary>
public class ReconciliationSweepBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReconciliationSweepBackgroundService> _logger;

    // How often the sweep runs.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    // Small delay before the first sweep so it doesn't run during startup.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    public ReconciliationSweepBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<ReconciliationSweepBackgroundService> logger)
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
                var reconciliation = scope.ServiceProvider.GetRequiredService<IReconciliationService>();

                var healed = await reconciliation.RetryFailedStatusCallbacksAsync(stoppingToken);
                if (healed > 0)
                    _logger.LogInformation("Reconciliation sweep re-acknowledged {Count} transfer status callback(s).", healed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation sweep failed.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (TaskCanceledException) { break; }
        }
    }
}
