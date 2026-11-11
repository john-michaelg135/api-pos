namespace Applications.Services;

/// <summary>
/// How long after a sale a refund can be requested. The products are food, so the window is short.
///
/// ── To change the refund window, edit the two numbers below. ──
/// The count is in calendar days, Philippine time, and the window always runs to the end of the
/// last day: 1 means "until 11:59 PM the next day" (a sale completed Nov 11 at 3 PM can be refunded
/// until Nov 12, 11:59 PM). 0 would mean "same day only". The POS screens read the resulting
/// deadline from the API, so nothing else needs editing.
/// </summary>
public static class RefundPolicy
{
    /// <summary>Walk-in / store sales: days after the sale was completed.</summary>
    public const int StoreWindowDays = 1;

    /// <summary>Institutional orders: days after the order was delivered.</summary>
    public const int InstitutionalWindowDays = 1;

    // The Philippines has no daylight saving, so a fixed offset is exact.
    private static readonly TimeSpan ManilaOffset = TimeSpan.FromHours(8);

    /// <summary>
    /// When the window starts. Store sales: the first time the order was marked Completed (so a
    /// later return to Completed, e.g. after a partial refund, doesn't restart it), or when it was
    /// created if it was completed at the counter. Institutional: the recorded delivery time, or
    /// the first Completed/Delivered status. Null when the order hasn't been delivered yet.
    /// </summary>
    public static DateTime? WindowStartUtc(
        string orderType,
        DateTime createdAtUtc,
        IEnumerable<(string NewStatus, DateTime At)> statusHistory,
        DateTime? deliveredAtUtc)
    {
        if (orderType == "Institutional")
        {
            return deliveredAtUtc ?? statusHistory
                .Where(h => h.NewStatus is "Completed" or "Delivered")
                .Select(h => (DateTime?)h.At)
                .Min();
        }

        return statusHistory
            .Where(h => h.NewStatus == "Completed")
            .Select(h => (DateTime?)h.At)
            .Min() ?? createdAtUtc;
    }

    /// <summary>
    /// The moment the window closes (UTC): midnight, Philippine time, after the last allowed day.
    /// A refund can be requested while now is before it. Null when no window applies yet.
    /// </summary>
    public static DateTime? DeadlineUtc(string orderType, DateTime? windowStartUtc)
    {
        if (windowStartUtc is null) return null;
        var days = orderType == "Institutional" ? InstitutionalWindowDays : StoreWindowDays;
        var startDay = DateOnly.FromDateTime(windowStartUtc.Value + ManilaOffset);
        var closesManila = startDay.AddDays(days + 1).ToDateTime(TimeOnly.MinValue);
        return DateTime.SpecifyKind(closesManila - ManilaOffset, DateTimeKind.Utc);
    }

    /// <summary>"Nov 12, 2026, 11:59 PM" for the last minute of the window, Philippine time.</summary>
    public static string Describe(DateTime deadlineUtc) =>
        (deadlineUtc + ManilaOffset - TimeSpan.FromMinutes(1)).ToString("MMM d, yyyy, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
}
