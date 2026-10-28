using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Applications.Interfaces;

namespace Applications.Services;

/// <summary>
/// Periodic end-of-day sweep: closes shifts no cashier ended once their business day is over.
/// The Money Audit list also runs the same close before loading, so a sleeping host can't leave
/// a past shift showing as open.
/// </summary>
public class ShiftAutoCloseBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ShiftAutoCloseBackgroundService> _logger;

    // How often the sweep runs.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);
    // Small delay before the first sweep so it doesn't run during startup.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    public ShiftAutoCloseBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<ShiftAutoCloseBackgroundService> logger)
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
                var autoClose = scope.ServiceProvider.GetRequiredService<IShiftAutoCloseService>();

                var closed = await autoClose.CloseOverdueShiftsAsync(stoppingToken);
                if (closed > 0)
                    _logger.LogInformation("End-of-day sweep auto-closed {Count} shift(s).", closed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "End-of-day shift sweep failed.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (TaskCanceledException) { break; }
        }
    }
}
