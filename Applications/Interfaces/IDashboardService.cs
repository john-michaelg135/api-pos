using Api.Contracts.Dashboard;

namespace Applications.Interfaces;

public interface IDashboardService
{
    // Owner dashboard: sales at a glance for a Philippine date range (no start date = all dates)
    Task<OwnerDashboardDto> GetOwnerDashboardAsync(DateOnly? dateFrom, DateOnly? dateTo, int? locationId, CancellationToken cancellationToken = default);
}
