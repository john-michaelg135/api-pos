using Api.Contracts.Analytics;

namespace Applications.Interfaces;

public interface IAnalyticsService
{
    // POS-021: Aggregated revenue by period (Day/Week/Month)
    Task<List<RevenueSummaryDto>> GetRevenueSummaryAsync(AnalyticsFilterDto filter);

    // POS-022: Sales breakdowns for dynamic filtering
    Task<List<SalesBreakdownDto>> GetSalesByLocationAsync(AnalyticsFilterDto filter);
    Task<List<SalesBreakdownDto>> GetSalesByVariationAsync(AnalyticsFilterDto filter);
    Task<List<SalesBreakdownDto>> GetSalesByChannelAsync(AnalyticsFilterDto filter);
}
