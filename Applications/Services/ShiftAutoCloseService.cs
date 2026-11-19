using Applications.Audit;
using Applications.Interfaces;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

/// <summary>
/// Closes shifts a cashier forgot to end. A shift still open once its business day is over
/// (midnight, Philippine time) is closed as of that midnight and flagged AutoClosed, so the
/// Money Audit can tell it apart from a cashier's own count. No cash was counted, so the
/// end-of-shift amount stays empty; sales, refunds and expected cash come from the day itself.
/// </summary>
public class ShiftAutoCloseService : IShiftAutoCloseService
{
    private static readonly AuditActor Job = AuditActor.Process("pos-shift-auto-close", "POS end-of-day shift auto-close");

    private readonly PosDbContext _db;
    private readonly IAuditLogService _audit;

    public ShiftAutoCloseService(PosDbContext db, IAuditLogService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<int> CloseOverdueShiftsAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

        var overdue = await _db.StartingCashEntries
            .Include(entry => entry.Location)
            .Where(entry => entry.EndShiftAt == null && entry.BusinessDate < today)
            .ToListAsync(cancellationToken);

        foreach (var entry in overdue)
        {
            // Midnight after the business day in Philippine time is 16:00 UTC on the business day.
            entry.EndShiftAt = entry.BusinessDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-8);
            entry.AutoClosed = true;
        }

        if (overdue.Count > 0) await _db.SaveChangesAsync(cancellationToken);

        // Attributed to the job even when the Money Audit page triggered the sweep.
        foreach (var entry in overdue)
            await _audit.RecordAsync(StartingCashAudit.Closed(entry, entry.Location?.LocationName, Job));

        return overdue.Count;
    }
}
