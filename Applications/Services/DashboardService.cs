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

    public async Task<OwnerDashboardDto> GetOwnerDashboardAsync(DateOnly? dateFrom, DateOnly? dateTo, int? locationId, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var nowManila = nowUtc + ManilaOffset;
        var today = DateOnly.FromDateTime(nowManila);

        // Same periods as the Sales Report. No start date means "all dates": from the first sale, with nothing to compare to.
        var hasComparison = dateFrom.HasValue;
        var to = dateTo ?? today;
        var from = dateFrom ?? await FirstSaleDateAsync(locationId, cancellationToken) ?? to;
        if (from > to) (from, to) = (to, from);

        // The previous period has the same length and ends the day before this one starts (as in the Sales Report).
        var lengthDays = to.DayNumber - from.DayNumber + 1;
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-(lengthDays - 1));

        var currentStart = ToUtc(from);
        var currentEnd = ToUtc(to.AddDays(1));
        var previousStart = ToUtc(previousFrom);
        var previousEnd = ToUtc(previousTo.AddDays(1));
        // A period still in progress is compared with the same elapsed time of the previous one
        // (today until 3 PM vs. yesterday until 3 PM), so the comparison isn't lopsided.
        if (currentEnd > nowUtc && nowUtc > currentStart)
            previousEnd = previousStart + (nowUtc - currentStart);

        var money = await LoadMoneyEventsAsync(hasComparison ? previousStart : currentStart, currentEnd, locationId, cancellationToken);
        var currentMoney = money.Where(e => e.At >= currentStart && e.At < currentEnd).ToList();
        var previousMoney = hasComparison ? money.Where(e => e.At >= previousStart && e.At < previousEnd).ToList() : new List<MoneyEvent>();

        // Orders that count as a sale: paid in full or with a downpayment, not voided/cancelled.
        var saleOrders = _db.Orders
            .AsNoTracking()
            .Where(o => !ExcludedOrderStatuses.Contains(o.OrderStatus))
            .Where(o => o.PaymentStatus == "Paid" || o.PaymentStatus == "Partial" || o.OrderStatus == "Completed")
            .Where(o => !locationId.HasValue || o.LocationId == locationId);

        // This period needs every order and its items (branches, channels, best sellers)...
        var currentOrders = await saleOrders
            .Where(o => o.CreatedAt >= currentStart && o.CreatedAt < currentEnd)
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

        // ...the comparison period only needs totals, so the database adds them up.
        var previousOrders = saleOrders.Where(o => o.CreatedAt >= previousStart && o.CreatedAt < previousEnd);
        var previousTotals = hasComparison
            ? await previousOrders
                .GroupBy(o => 1)
                .Select(g => new { Count = g.Count(), Amount = g.Sum(o => o.TotalAmount) })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var previousCount = previousTotals?.Count ?? 0;
        var previousItemsSold = hasComparison
            ? await _db.OrderItems
                .AsNoTracking()
                .Where(i => previousOrders.Any(o => o.OrderId == i.OrderId))
                .SumAsync(i => (int?)i.Quantity, cancellationToken) ?? 0
            : 0;

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
            DateFrom = from,
            DateTo = to,
            PreviousDateFrom = hasComparison ? previousFrom : null,
            PreviousDateTo = hasComparison ? previousTo : null,
            LocationId = locationId,
            GeneratedAt = nowUtc,
            Sales = new DashboardKpiDto
            {
                Current = currentMoney.Sum(e => e.Amount),
                Previous = previousMoney.Sum(e => e.Amount),
            },
            Orders = new DashboardKpiDto { Current = currentOrders.Count, Previous = previousCount },
            AverageSale = new DashboardKpiDto
            {
                Current = currentOrders.Count == 0 ? 0 : Math.Round(currentOrders.Average(o => o.TotalAmount), 2),
                Previous = previousCount == 0 ? 0 : Math.Round(previousTotals!.Amount / previousCount, 2),
            },
            ItemsSold = new DashboardKpiDto
            {
                Current = currentOrders.Sum(o => o.Items.Sum(i => i.Quantity)),
                Previous = previousItemsSold,
            },
        };

        // ── Trend: by hour for a single day, by day up to two months, by month beyond that ──
        // Each comparison point is the same slot shifted back by the period's length.
        var shift = currentStart - previousStart;
        var buckets = new List<(DateTime Start, DateTime End, string Label)>();
        if (lengthDays == 1)
        {
            dto.Granularity = "hour";
            for (var hour = 0; hour < 24; hour++)
                buckets.Add((currentStart.AddHours(hour), currentStart.AddHours(hour + 1), new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt")));
        }
        else if (lengthDays <= 62)
        {
            dto.Granularity = "day";
            for (var date = from; date <= to; date = date.AddDays(1))
                buckets.Add((ToUtc(date), ToUtc(date.AddDays(1)), date.ToString("MMM d")));
        }
        else
        {
            dto.Granularity = "month";
            for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
            {
                var start = month < from ? from : month;
                var end = month.AddMonths(1) > to ? to.AddDays(1) : month.AddMonths(1);
                buckets.Add((ToUtc(start), ToUtc(end), month.ToString(month.Year == today.Year ? "MMM" : "MMM yyyy")));
            }
        }

        foreach (var (start, end, label) in buckets)
        {
            dto.Trend.Add(new DashboardTrendPointDto
            {
                Label = label,
                Sales = currentMoney.Where(e => e.At >= start && e.At < end).Sum(e => e.Amount),
                // The chart shows the whole previous period, not only the part the totals compare against.
                PreviousSales = hasComparison
                    ? money.Where(e => e.At >= start - shift && e.At < end - shift && e.At < currentStart).Sum(e => e.Amount)
                    : 0,
                IsFuture = start > nowUtc,
            });
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
                // A cancelled order's forfeited (non-refundable) downpayment is still revenue.
                && (!ExcludedOrderStatuses.Contains(p.Order.OrderStatus) || p.Order.PaymentStatus == "Forfeited")
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

        // Older Paid orders without a payment row were given one by the
        // BackfillLegacyPaidOrderPayments migration, so payments alone cover every sale.

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
            .Concat(itemRefunds.Select(r => new MoneyEvent(r.At, -r.Amount, r.LocationId, ChannelOf(r.OrderSource, r.OrderType))))
            .Concat(wholeOrderRefunds.Select(o => new MoneyEvent(o.At, -o.TotalAmount, o.LocationId, ChannelOf(o.OrderSource, o.OrderType))))
            .ToList();
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

    // Philippine date of the first order, the start of "All dates".
    private async Task<DateOnly?> FirstSaleDateAsync(int? locationId, CancellationToken cancellationToken)
    {
        var first = await _db.Orders
            .AsNoTracking()
            .Where(o => !locationId.HasValue || o.LocationId == locationId)
            .MinAsync(o => (DateTime?)o.CreatedAt, cancellationToken);
        return first.HasValue ? DateOnly.FromDateTime(first.Value + ManilaOffset) : null;
    }

    private static string ChannelOf(string? orderSource, string? orderType) =>
        orderSource == "Ecommerce" || orderType == "Online" ? "Online"
        : orderType == "Institutional" ? "Institutional"
        : "Walk-in";

    private static DateTime ToUtc(DateOnly manilaDate) =>
        DateTime.SpecifyKind(manilaDate.ToDateTime(TimeOnly.MinValue) - ManilaOffset, DateTimeKind.Utc);
}
