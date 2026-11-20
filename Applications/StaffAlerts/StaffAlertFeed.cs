using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.StaffAlerts;

public sealed record StaffAlertItem(
    long Id,
    string Kind,
    string Module,
    string Title,
    string Body,
    string Link,
    int? LocationId,
    int? OrderId,
    string? TransferId,
    int? VariationId,
    bool Partial,
    DateTime CreatedAt,
    bool Read);

public sealed record StaffAlertPage(IReadOnlyList<StaffAlertItem> Items, string? NextCursor, int Unread);

/// <summary>
/// The POS bell for one signed-in viewer. Every read, count and mark goes through
/// <see cref="StaffAlertAccess.VisibleTo"/> with the viewer's current grants and branches, so an
/// alert from another branch or Module is invisible and cannot be marked. Reading never approves a
/// refund or completes a transfer.
/// </summary>
public sealed class StaffAlertFeed
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    private const int MarkAllLimit = 500;

    private readonly PosDbContext _db;
    private readonly TimeProvider _clock;

    public StaffAlertFeed(PosDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<StaffAlertPage> ListAsync(StaffViewer viewer, string? cursor, int? limit, bool unreadOnly, CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var query = StaffAlertAccess.VisibleTo(_db.StaffAlerts.AsNoTracking(), viewer);
        if (unreadOnly) query = query.Where(a => a.ReadAt == null);
        if (TryParseCursor(cursor, out var before)) query = query.Where(a => a.Id < before);

        var rows = await query.OrderByDescending(a => a.Id).Take(size + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > size;
        var page = rows.Take(size).Select(ToItem).ToList();
        var unread = await UnreadCountAsync(viewer, cancellationToken);
        return new StaffAlertPage(page, hasMore ? page[^1].Id.ToString() : null, unread);
    }

    public Task<int> UnreadCountAsync(StaffViewer viewer, CancellationToken cancellationToken = default) =>
        StaffAlertAccess.VisibleTo(_db.StaffAlerts.AsNoTracking(), viewer)
            .CountAsync(a => a.ReadAt == null, cancellationToken);

    /// <summary>False when the alert doesn't exist or isn't the viewer's to see.</summary>
    public async Task<bool> MarkReadAsync(StaffViewer viewer, long id, CancellationToken cancellationToken = default)
    {
        var alert = await StaffAlertAccess.VisibleTo(_db.StaffAlerts, viewer)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (alert is null) return false;
        if (alert.ReadAt is not null) return true;

        await MarkAsync([alert], viewer, cancellationToken);
        return true;
    }

    public async Task<int> MarkAllReadAsync(StaffViewer viewer, CancellationToken cancellationToken = default)
    {
        var alerts = await StaffAlertAccess.VisibleTo(_db.StaffAlerts, viewer)
            .Where(a => a.ReadAt == null)
            .OrderByDescending(a => a.Id)
            .Take(MarkAllLimit)
            .ToListAsync(cancellationToken);
        if (alerts.Count == 0) return 0;

        await MarkAsync(alerts, viewer, cancellationToken);
        return alerts.Count;
    }

    private async Task MarkAsync(List<StaffAlert> alerts, StaffViewer viewer, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var alert in alerts)
        {
            alert.ReadAt = now;
            alert.ReadByPortalUserId = viewer.PortalUserId;
        }

        // Only these alerts' own Portal copies are due to be marked read.
        var ids = alerts.Select(a => a.Id).ToArray();
        var copies = await _db.StaffAlertCopies
            .Where(c => ids.Contains(c.AlertId) && (c.RemoteId != null || c.State == StaffAlertCopyStates.Pending))
            .ToListAsync(cancellationToken);
        foreach (var copy in copies)
        {
            copy.Done = false;
            copy.NextAttemptAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        StaffAlertSignal.RequestRefresh();
    }

    private static bool TryParseCursor(string? cursor, out long before) =>
        long.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out before) && before > 0;

    private static StaffAlertItem ToItem(StaffAlert a) => new(
        a.Id, a.Kind, a.Module, a.Title, a.Body, a.LinkPath, a.LocationId, a.OrderId, a.TransferId, a.VariationId,
        a.Partial, DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc), a.ReadAt is not null);
}
