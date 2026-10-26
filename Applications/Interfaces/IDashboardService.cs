using Api.Contracts.Dashboard;

namespace Applications.Interfaces;

public interface IDashboardService
{
    // Owner dashboard: sales at a glance for today / this week / this month
    Task<OwnerDashboardDto> GetOwnerDashboardAsync(string? period, int? locationId, CancellationToken cancellationToken = default);
}
