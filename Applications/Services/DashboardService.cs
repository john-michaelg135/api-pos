using Api.Contracts.Dashboard;
using Applications.Interfaces;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

/// <summary>
/// Builds the owner's dashboard. "Sales" is money collected: successful payments
/// (cash, GCash, downpayments, settled balances) minus approved refunds, the same
/// rule the end-of-shift count uses. Days and hours are Philippine time (UTC+8).
/// </summary>
public class DashboardService : IDashboardService
{
    private static readonly TimeSpan ManilaOffset = TimeSpan.FromHours(8);
    private static readonly string[] ExcludedOrderStatuses = { "Voided", "Cancelled" };

    private readonly PosDbContext _db;

    public DashboardService(PosDbContext db)
    {
        _db = db;
    }

    // A single money movement: a payment received (+) or a refund paid out (−).
    private sealed record MoneyEvent(DateTime At, decimal Amount, int? LocationId, string Channel);

    public async Task<OwnerDashboardDto> GetOwnerDashboardAsync(string? period, int? locationId, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var nowManila = nowUtc + ManilaOffset;
        var today = DateOnly.FromDateTime(nowManila);

        period = period?.Trim().ToLowerInvariant() switch
        {
            "week" => "week",
            "month" => "month",
            _ => "today",
        };

        // Compare like with like: the same number of elapsed days in the previous period.
        DateOnly from, previousFrom;
        switch (period)
        {
            case "week":
                var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
                from = today.AddDays(-daysSinceMonday);
                previousFrom = from.AddDays(-7);
                break;
            case "month":
                from = new DateOnly(today.Year, today.Month, 1);
                previousFrom = from.AddMonths(-1);
                break;
            default:
                from = today;
                previousFrom = today.AddDays(-1);
                break;
        }

        var elapsedDays = today.DayNumber - from.DayNumber;
        var previousTo = previousFrom.AddDays(elapsedDays);
        // A short previous month (e.g. Feb vs. Mar 31) must not spill into the current one.
        if (previousTo >= from) previousTo = from.AddDays(-1);

        var currentStart = ToUtc(from);
        var currentEnd = ToUtc(today.AddDays(1));
        var previousStart = ToUtc(previousFrom);
        // For "today" compare against yesterday up to the same time of day.
        var previousEnd = period == "today" ? nowUtc.AddDays(-1) : ToUtc(previousTo.AddDays(1));

        var money = await LoadMoneyEventsAsync(previousStart, currentEnd, locationId, cancellationToken);
        var currentMoney = money.Where(e => e.At >= currentStart && e.At < currentEnd).ToList();
        var previousMoney = money.Where(e => e.At >= previousStart && e.At < previousEnd).ToList();

        // Orders that count as a sale: paid in full or with a downpayment, not voided/cancelled.
        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.CreatedAt >= previousStart && o.CreatedAt < currentEnd)
            .Where(o => !ExcludedOrderStatuses.Contains(o.OrderStatus))
            .Where(o => o.PaymentStatus == "Paid" || o.PaymentStatus == "Partial" || o.OrderStatus == "Completed")
            .Where(o => !locationId.HasValue || o.LocationId == locationId)
            .Select(o => new
            {
                o.OrderId,
                o.CreatedAt,
                o.TotalAmount,
                o.LocationId,
                Channel = o.OrderSource == "Ecommerce" || o.OrderType == "Online" ? "Online"
                    : o.OrderType == "Institutional" ? "Institutional"
                    : "Walk-in",
                Items = o.OrderItems.Select(i => new
                {
                    i.Quantity,
                    i.Subtotal,
                    ProductName = i.ProductVariation != null && i.ProductVariation.Product != null ? i.ProductVariation.Product.ProductName : "",
                    VariationName = i.ProductVariation != null ? i.ProductVariation.VariationName : "",
                }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var currentOrders = orders.Where(o => o.CreatedAt >= currentStart && o.CreatedAt < currentEnd).ToList();
        var previousOrders = orders.Where(o => o.CreatedAt >= previousStart && o.CreatedAt < previousEnd).ToList();

        var locations = await _db.Locations
            .AsNoTracking()
            .Where(l => l.IsActive || (locationId.HasValue && l.LocationId == locationId))
            .Where(l => !locationId.HasValue || l.LocationId == locationId)
            .OrderBy(l => l.LocationName)
            .Select(l => new { l.LocationId, l.LocationName, IsCommissary = l.LocationType == "Commissary" })
            .ToListAsync(cancellationToken);
        // The Commissary fulfils online orders and never opens a shift, so it is not a branch for "today".
        var branchNames = locations.Where(l => !l.IsCommissary).ToDictionary(l => l.LocationId, l => l.LocationName);

        var dto = new OwnerDashboardDto
        {
            Period = period,
            DateFrom = from,
            DateTo = today,
            PreviousDateFrom = previousFrom,
            PreviousDateTo = period == "today" ? previousFrom : previousTo,
            LocationId = locationId,
            GeneratedAt = nowUtc,
            Sales = new DashboardKpiDto
            {
                Current = currentMoney.Sum(e => e.Amount),
                Previous = previousMoney.Sum(e => e.Amount),
            },
            Orders = new DashboardKpiDto { Current = currentOrders.Count, Previous = previousOrders.Count },
            AverageSale = new DashboardKpiDto
            {
                Current = currentOrders.Count == 0 ? 0 : Math.Round(currentOrders.Average(o => o.TotalAmount), 2),
                Previous = previousOrders.Count == 0 ? 0 : Math.Round(previousOrders.Average(o => o.TotalAmount), 2),
            },
            ItemsSold = new DashboardKpiDto
            {
                Current = currentOrders.Sum(o => o.Items.Sum(i => i.Quantity)),
                Previous = previousOrders.Sum(o => o.Items.Sum(i => i.Quantity)),
            },
        };

        // ── Trend: by hour for today, by day otherwise ──
        if (period == "today")
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var start = currentStart.AddHours(hour);
                var previous = previousStart.AddHours(hour);
                dto.Trend.Add(new DashboardTrendPointDto
                {
                    Label = new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt"),
                    Sales = currentMoney.Where(e => e.At >= start && e.At < start.AddHours(1)).Sum(e => e.Amount),
                    PreviousSales = money.Where(e => e.At >= previous && e.At < previous.AddHours(1)).Sum(e => e.Amount),
                    IsFuture = start > nowUtc,
                });
            }
        }
        else
        {
            var periodLength = period == "week" ? 7 : DateTime.DaysInMonth(from.Year, from.Month);
            for (var day = 0; day < periodLength; day++)
            {
                var date = from.AddDays(day);
                var start = ToUtc(date);
                var previousDate = previousFrom.AddDays(day);
                var previousDayStart = ToUtc(previousDate);
                // Skip comparison days that fall outside the previous period (e.g. day 31 vs. a 30-day month).
                var hasPrevious = period == "week" || previousDate.Month == previousFrom.Month;
                dto.Trend.Add(new DashboardTrendPointDto
                {
                    Label = date.ToString("MMM d"),
                    Sales = currentMoney.Where(e => e.At >= start && e.At < start.AddDays(1)).Sum(e => e.Amount),
                    PreviousSales = hasPrevious
                        ? money.Where(e => e.At >= previousDayStart && e.At < previousDayStart.AddDays(1)).Sum(e => e.Amount)
                        : 0,
                    IsFuture = date > today,
                });
            }
        }

        // ── Sales by branch and by channel ──
        dto.SalesByBranch = locations
            .Select(l => new DashboardBranchSalesDto
            {
                LocationId = l.LocationId,
                Name = l.IsCommissary ? $"{l.LocationName} (online orders)" : l.LocationName,
                Sales = currentMoney.Where(e => e.LocationId == l.LocationId).Sum(e => e.Amount),
                Orders = currentOrders.Count(o => o.LocationId == l.LocationId),
            })
            .Where(b => b.Sales != 0 || b.Orders > 0 || !locations.First(l => l.LocationId == b.LocationId).IsCommissary)
            .OrderByDescending(b => b.Sales)
            .ToList();

        dto.SalesByChannel = new[] { "Walk-in", "Institutional", "Online" }
            .Select(channel => new DashboardChannelSalesDto
            {
                Channel = channel,
                Sales = currentMoney.Where(e => e.Channel == channel).Sum(e => e.Amount),
                Orders = currentOrders.Count(o => o.Channel == channel),
            })
            .ToList();

        // ── Top products by value sold this period ──
        dto.TopProducts = currentOrders
            .SelectMany(o => o.Items)
            .GroupBy(i => new { i.ProductName, i.VariationName })
            .Select(g => new DashboardTopProductDto
            {
                ProductName = g.Key.ProductName,
                VariationName = g.Key.VariationName,
                UnitsSold = g.Sum(i => i.Quantity),
                Sales = g.Sum(i => i.Subtotal),
            })
            .OrderByDescending(p => p.Sales)
            .ThenByDescending(p => p.UnitsSold)
            .Take(5)
            .ToList();

        dto.Attention = await LoadAttentionAsync(locationId, cancellationToken);
        dto.BranchesToday = await LoadBranchesTodayAsync(today, branchNames, money, nowUtc, cancellationToken);

        return dto;
    }

    // Payments received and refunds paid out, mirroring the end-of-shift sales rule.
    private async Task<List<MoneyEvent>> LoadMoneyEventsAsync(DateTime start, DateTime end, int? locationId, CancellationToken cancellationToken)
    {
        var payments = await _db.Payments
            .AsNoTracking()
            .Where(p => (p.PaymentStatus == "Success" || p.PaymentStatus == "Paid")
                && p.PaidAt >= start && p.PaidAt < end
                && p.Order != null
                && !ExcludedOrderStatuses.Contains(p.Order.OrderStatus)
                && (!locationId.HasValue || p.Order.LocationId == locationId))
            .Select(p => new
            {
                p.PaidAt,
                p.AmountPaid,
                p.Order!.LocationId,
                p.Order.OrderSource,
                p.Order.OrderType,
            })
            .ToListAsync(cancellationToken);

        // Older orders marked Paid before payment rows were recorded.
        var paidWithoutPayments = await _db.Orders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == "Paid"
                && o.CreatedAt >= start && o.CreatedAt < end
                && !ExcludedOrderStatuses.Contains(o.OrderStatus)
                && (!locationId.HasValue || o.LocationId == locationId)
                && !_db.Payments.Any(p => p.OrderId == o.OrderId && (p.PaymentStatus == "Success" || p.PaymentStatus == "Paid")))
            .Select(o => new { o.CreatedAt, o.TotalAmount, o.LocationId, o.OrderSource, o.OrderType })
            .ToListAsync(cancellationToken);

        var itemRefunds = await (
            from refund in _db.RefundRequests.AsNoTracking()
            join item in _db.OrderItems.AsNoTracking()
                on new { refund.OrderId, refund.VariationId } equals new { item.OrderId, item.VariationId }
            join order in _db.Orders.AsNoTracking() on refund.OrderId equals order.OrderId
            where refund.Status == "Approved"
                && refund.ApprovedAt.HasValue
                && refund.ApprovedAt.Value >= start && refund.ApprovedAt.Value < end
                && (!locationId.HasValue || refund.LocationId == locationId)
            select new
            {
                At = refund.ApprovedAt!.Value,
                Amount = item.UnitPrice * refund.QuantityToReturn,
                LocationId = (int?)refund.LocationId,
                order.OrderSource,
                order.OrderType,
            }
        ).ToListAsync(cancellationToken);

        var wholeOrderRefunds = await _db.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus == "Refunded"
                && o.ApprovedAt.HasValue
                && o.ApprovedAt.Value >= start && o.ApprovedAt.Value < end
                && (!locationId.HasValue || o.LocationId == locationId)
                && !_db.RefundRequests.Any(r => r.OrderId == o.OrderId && r.Status == "Approved"))
            .Select(o => new { At = o.ApprovedAt!.Value, o.TotalAmount, o.LocationId, o.OrderSource, o.OrderType })
            .ToListAsync(cancellationToken);

        return payments.Select(p => new MoneyEvent(p.PaidAt, p.AmountPaid, p.LocationId, ChannelOf(p.OrderSource, p.OrderType)))
            .Concat(paidWithoutPayments.Select(o => new MoneyEvent(o.CreatedAt, o.TotalAmount, o.LocationId, ChannelOf(o.OrderSource, o.OrderType))))
            .Concat(itemRefunds.Select(r => new MoneyEvent(r.At, -r.Amount, r.LocationId, ChannelOf(r.OrderSource, r.OrderType))))
            .Concat(wholeOrderRefunds.Select(o => new MoneyEvent(o.At, -o.TotalAmount, o.LocationId, ChannelOf(o.OrderSource, o.OrderType))))
            .ToList();
    }

    private async Task<DashboardAttentionDto> LoadAttentionAsync(int? locationId, CancellationToken cancellationToken)
    {
        var orders = _db.Orders.AsNoTracking().Where(o => !locationId.HasValue || o.LocationId == locationId);

        var partials = await orders
            .Where(o => o.PaymentStatus == "Partial" && !ExcludedOrderStatuses.Contains(o.OrderStatus))
            .Select(o => new
            {
                o.TotalAmount,
                Paid = o.Payments.Where(p => p.PaymentStatus == "Success").Sum(p => (decimal?)p.AmountPaid) ?? 0,
            })
            .ToListAsync(cancellationToken);

        var refundRequestOrders = await orders.CountAsync(o => o.OrderStatus == "Refund Requested", cancellationToken);
        var pendingItemRefunds = await _db.RefundRequests
            .AsNoTracking()
            .Where(r => r.Status == "Pending" && (!locationId.HasValue || r.LocationId == locationId))
            .Where(r => !_db.Orders.Any(o => o.OrderId == r.OrderId && o.OrderStatus == "Refund Requested"))
            .Select(r => r.OrderId)
            .Distinct()
            .CountAsync(cancellationToken);

        var lowStock = await _db.Stocks
            .AsNoTracking()
            .Where(s => s.MinThreshold > 0 && s.Quantity < s.MinThreshold)
            .Where(s => !locationId.HasValue || s.LocationId == locationId)
            .Where(s => s.Location != null && s.Location.IsActive)
            .OrderBy(s => (double)s.Quantity / s.MinThreshold)
            .ThenBy(s => s.Quantity)
            .Select(s => new DashboardLowStockItemDto
            {
                ProductName = s.Variation != null && s.Variation.Product != null ? s.Variation.Product.ProductName : "",
                VariationName = s.Variation != null ? s.Variation.VariationName : "",
                BranchName = s.Location != null ? s.Location.LocationName : "",
                Quantity = s.Quantity,
                ReorderLevel = s.MinThreshold,
            })
            .ToListAsync(cancellationToken);

        return new DashboardAttentionDto
        {
            UnpaidBalanceAmount = partials.Sum(o => Math.Max(0, o.TotalAmount - o.Paid)),
            UnpaidBalanceOrders = partials.Count,
            ReadyToShip = await orders.CountAsync(o => o.OrderStatus == "Paid", cancellationToken),
            OnlineOrdersToApprove = await orders.CountAsync(o => o.OrderSource == "Ecommerce" && (o.OrderStatus == "Pending" || o.OrderStatus == "Awaiting Stock"), cancellationToken),
            RefundRequests = refundRequestOrders + pendingItemRefunds,
            VoidRequests = await orders.CountAsync(o => o.OrderStatus == "Void Requested", cancellationToken),
            LowStockCount = lowStock.Count,
            LowStockItems = lowStock.Take(5).ToList(),
        };
    }

    private async Task<List<DashboardBranchTodayDto>> LoadBranchesTodayAsync(
        DateOnly today,
        Dictionary<int, string> locationNames,
        List<MoneyEvent> money,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var ids = locationNames.Keys.ToList();
        var shifts = await _db.StartingCashEntries
            .AsNoTracking()
            .Where(s => s.BusinessDate == today && ids.Contains(s.LocationId))
            .Select(s => new { s.LocationId, s.EndShiftAt, s.EndShiftAmount, s.ExpectedCash })
            .ToListAsync(cancellationToken);

        var todayStart = ToUtc(today);
        return locationNames
            .Select(l =>
            {
                var branchShifts = shifts.Where(s => s.LocationId == l.Key).ToList();
                var closed = branchShifts.Where(s => s.EndShiftAt.HasValue && s.EndShiftAmount.HasValue && s.ExpectedCash.HasValue).ToList();
                return new DashboardBranchTodayDto
                {
                    LocationId = l.Key,
                    Name = l.Value,
                    ShiftStatus = branchShifts.Count == 0 ? "Not opened"
                        : branchShifts.Any(s => !s.EndShiftAt.HasValue) ? "Open"
                        : "Closed",
                    SalesToday = money.Where(e => e.LocationId == l.Key && e.At >= todayStart && e.At <= nowUtc).Sum(e => e.Amount),
                    CashOverShort = closed.Count == 0 ? null : closed.Sum(s => s.EndShiftAmount!.Value - s.ExpectedCash!.Value),
                };
            })
            .OrderBy(b => b.Name)
            .ToList();
    }

    private static string ChannelOf(string? orderSource, string? orderType) =>
        orderSource == "Ecommerce" || orderType == "Online" ? "Online"
        : orderType == "Institutional" ? "Institutional"
        : "Walk-in";

    private static DateTime ToUtc(DateOnly manilaDate) =>
        DateTime.SpecifyKind(manilaDate.ToDateTime(TimeOnly.MinValue) - ManilaOffset, DateTimeKind.Utc);
}
