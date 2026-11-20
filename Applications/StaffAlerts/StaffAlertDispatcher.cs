using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.StaffAlerts;

/// <summary>
/// Delivers saved alerts to the Portal and keeps read state in step, after the business commit.
///
/// 1. Audience: on the first successful directory read, each alert's recipients are snapshotted once
///    (an empty audience counts). A directory outage leaves the audience unresolved.
/// 2. Copies: each recipient's copy is its own retry unit. Eligibility is rechecked before every
///    unsent delivery; a recipient who lost access gets no new copy, only a lookup of an earlier one
///    (an uncertain send). The remote UUID is kept, so retries and restarts recover the same copy.
/// 3. Read sync: a POS read marks every linked copy read; an eligible recipient reading their Portal
///    copy marks the alert read, which then reaches the other copies. A personal decision's read
///    state is its requester's alone.
/// 4. Refresh notice: one content-free message on the POS staff channel after new alerts.
/// </summary>
public sealed class StaffAlertDispatcher
{
    private const int AudienceBatch = 50;
    private const int CopyBatch = 100;
    private const int MaxConsecutiveTransportFailures = 3;
    private static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan NudgeWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(3);
    private static readonly TimeSpan StopPollingAfter = TimeSpan.FromDays(14);

    private readonly PosDbContext _db;
    private readonly IStaffAlertGateway _gateway;
    private readonly StaffAlertOptions _options;
    private readonly ILogger<StaffAlertDispatcher> _logger;
    private readonly TimeProvider _clock;

    public StaffAlertDispatcher(PosDbContext db, IStaffAlertGateway gateway, StaffAlertOptions options,
        ILogger<StaffAlertDispatcher> logger, TimeProvider? clock = null)
    {
        _db = db;
        _gateway = gateway;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public sealed record Summary(int AudiencesResolved, int CopiesSent, int CopiesSynced, bool Nudged);

    public async Task<Summary> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.SendsEnabled) return new Summary(0, 0, 0, false);
        var started = Now;

        var unresolved = await _db.StaffAlerts
            .Where(a => a.AudienceResolvedAt == null)
            .OrderBy(a => a.Id)
            .Take(AudienceBatch)
            .ToListAsync(cancellationToken);
        var anyDue = await DueCopies().AnyAsync(cancellationToken);

        Dictionary<string, StaffViewer>? viewers = null;
        if (unresolved.Count > 0 || anyDue)
        {
            try
            {
                viewers = await LoadViewersAsync(cancellationToken);
            }
            catch (StaffAlertGatewayException ex)
            {
                _logger.LogWarning("Portal recipient directory unavailable ({Category}); audiences stay unresolved.", ex.Category);
            }
        }

        var resolved = 0;
        if (viewers is not null)
        {
            foreach (var alert in unresolved)
            {
                if (await ResolveAudienceAsync(alert, viewers, cancellationToken)) resolved++;
            }
        }

        var copies = await DueCopies()
            .Include(c => c.Alert)
            .OrderBy(c => c.NextAttemptAt).ThenBy(c => c.Id)
            .Take(CopyBatch)
            .ToListAsync(cancellationToken);

        int sent = 0, synced = 0, transportFailures = 0;
        foreach (var copy in copies)
        {
            if (Now - started > RunBudget || transportFailures >= MaxConsecutiveTransportFailures) break;
            try
            {
                var outcome = await ProcessCopyAsync(copy, viewers, cancellationToken);
                if (outcome == CopyOutcome.Sent) sent++;
                else if (outcome == CopyOutcome.Synced) synced++;
                transportFailures = 0;
            }
            catch (StaffAlertGatewayException ex)
            {
                if (ex.Transport) transportFailures++;
                await RetryLaterAsync(copy, ex.Category, cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Could not save Portal copy state for alert {AlertId}.", copy.AlertId);
                _db.ChangeTracker.Clear();
            }
        }

        var nudged = await NudgeAsync(cancellationToken);
        return new Summary(resolved, sent, synced, nudged);
    }

    private IQueryable<StaffAlertCopy> DueCopies()
    {
        var now = Now;
        return _db.StaffAlertCopies.Where(c => !c.Done && c.NextAttemptAt <= now);
    }

    private async Task<Dictionary<string, StaffViewer>> LoadViewersAsync(CancellationToken cancellationToken)
    {
        var staff = await _gateway.RecipientsAsync(cancellationToken);
        var ids = staff.Select(s => s.Id).Distinct().ToArray();
        var branches = (await _db.PosUserLocations.AsNoTracking()
                .Where(l => ids.Contains(l.AuthUserId))
                .Select(l => new { l.AuthUserId, l.LocationId })
                .ToListAsync(cancellationToken))
            .GroupBy(l => l.AuthUserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<int>)g.Select(l => l.LocationId).Distinct().ToArray());

        var viewers = new Dictionary<string, StaffViewer>(StringComparer.Ordinal);
        foreach (var person in staff)
        {
            viewers[person.Id] = new StaffViewer(person.Id, person.Role, person.Modules,
                branches.GetValueOrDefault(person.Id) ?? Array.Empty<int>());
        }
        return viewers;
    }

    private async Task<bool> ResolveAudienceAsync(StaffAlert alert, Dictionary<string, StaffViewer> viewers, CancellationToken cancellationToken)
    {
        var now = Now;
        var audience = viewers.Values
            .Where(v => StaffAlertAccess.CanSee(alert, v))
            .Select(v => v.PortalUserId)
            .Distinct()
            .ToList();
        var added = audience.Select(userId => new StaffAlertCopy
        {
            AlertId = alert.Id,
            PortalUserId = userId,
            State = StaffAlertCopyStates.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        }).ToList();
        _db.StaffAlertCopies.AddRange(added);
        alert.AudienceResolvedAt = now;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Another worker snapshotted it first; its audience stands.
            _logger.LogWarning(ex, "Audience for alert {AlertId} was already resolved elsewhere.", alert.Id);
            foreach (var copy in added) _db.Entry(copy).State = EntityState.Detached;
            await _db.Entry(alert).ReloadAsync(cancellationToken);
            return false;
        }
    }

    private enum CopyOutcome { Sent, Synced, Waiting, Closed }

    private async Task<CopyOutcome> ProcessCopyAsync(StaffAlertCopy copy, Dictionary<string, StaffViewer>? viewers, CancellationToken cancellationToken)
    {
        var alert = copy.Alert!;
        var now = Now;
        copy.LastAttemptAt = now;
        copy.LastError = null; // a failure below is recorded by RetryLaterAsync instead
        var key = IdempotencyKey(alert);
        var viewer = viewers?.GetValueOrDefault(copy.PortalUserId);
        var eligible = viewer is not null && StaffAlertAccess.CanSee(alert, viewer);
        CopyOutcome outcome;

        if (copy.RemoteId is null)
        {
            if (alert.ReadAt is not null)
            {
                // Read in POS before delivery: never create a copy, but close one an earlier attempt may have made.
                var earlier = await _gateway.FindAsync(copy.PortalUserId, key, cancellationToken);
                if (earlier is null)
                {
                    Close(copy, StaffAlertCopyStates.Skipped);
                    outcome = CopyOutcome.Closed;
                }
                else
                {
                    Linked(copy, earlier.Id, now);
                    if (!earlier.Read) await _gateway.MarkReadAsync(copy.PortalUserId, earlier.Id, cancellationToken);
                    copy.Done = true;
                    outcome = CopyOutcome.Synced;
                }
            }
            else if (viewers is null)
            {
                throw new StaffAlertGatewayException("directory_unavailable");
            }
            else if (eligible)
            {
                var remote = await _gateway.SendAsync(new PortalCopyRequest(
                    key, alert.Kind, alert.Title, alert.Body, _options.AbsoluteLink(alert.LinkPath), copy.PortalUserId), cancellationToken);
                Linked(copy, remote.Id, now);
                copy.SentAt = now;
                if (remote.Read)
                {
                    await ImportReadAsync(alert, copy, now, cancellationToken);
                    copy.Done = true;
                }
                outcome = CopyOutcome.Sent;
            }
            else
            {
                // Access was revoked before delivery: recover an uncertain earlier send, never create one.
                var earlier = await _gateway.FindAsync(copy.PortalUserId, key, cancellationToken);
                if (earlier is null)
                {
                    Close(copy, StaffAlertCopyStates.Withheld);
                    outcome = CopyOutcome.Closed;
                }
                else
                {
                    Linked(copy, earlier.Id, now);
                    outcome = CopyOutcome.Synced;
                }
            }
        }
        else if (alert.ReadAt is not null)
        {
            await _gateway.MarkReadAsync(copy.PortalUserId, copy.RemoteId.Value, cancellationToken);
            copy.Done = true;
            outcome = CopyOutcome.Synced;
        }
        else
        {
            var remote = await _gateway.StatusAsync(copy.PortalUserId, copy.RemoteId.Value, cancellationToken);
            if (remote is null)
            {
                copy.Done = true;
                copy.LastError = "copy_missing";
                outcome = CopyOutcome.Closed;
            }
            else if (remote.Read && viewers is not null)
            {
                // Only an eligible reviewer (or the requester) reading their copy clears the alert.
                if (eligible) await ImportReadAsync(alert, copy, now, cancellationToken);
                copy.Done = true;
                outcome = CopyOutcome.Synced;
            }
            else
            {
                outcome = CopyOutcome.Waiting;
            }
        }

        if (!copy.Done)
        {
            var age = now - alert.CreatedAt;
            if (age > StopPollingAfter) copy.Done = true; // a later POS read reopens it
            else copy.NextAttemptAt = now + PollInterval(age);
        }
        await _db.SaveChangesAsync(cancellationToken);
        return outcome;
    }

    private static void Linked(StaffAlertCopy copy, Guid remoteId, DateTime now)
    {
        copy.RemoteId = remoteId;
        copy.State = StaffAlertCopyStates.Sent;
        copy.Attempts = 0;
        copy.NextAttemptAt = now;
    }

    private static void Close(StaffAlertCopy copy, string state)
    {
        copy.State = state;
        copy.Done = true;
    }

    /// <summary>The alert becomes read; its other linked copies are due to be marked read in the same save.</summary>
    private async Task ImportReadAsync(StaffAlert alert, StaffAlertCopy reader, DateTime now, CancellationToken cancellationToken)
    {
        if (alert.ReadAt is not null) return;
        alert.ReadAt = now;
        alert.ReadByPortalUserId = reader.PortalUserId;
        var siblings = await _db.StaffAlertCopies
            .Where(c => c.AlertId == alert.Id && c.Id != reader.Id)
            .ToListAsync(cancellationToken);
        foreach (var sibling in siblings.Where(s => s.RemoteId is not null || s.State == StaffAlertCopyStates.Pending))
        {
            sibling.Done = false;
            sibling.NextAttemptAt = now;
        }
        StaffAlertSignal.RequestRefresh();
    }

    private async Task RetryLaterAsync(StaffAlertCopy copy, string category, CancellationToken cancellationToken)
    {
        var now = Now;
        copy.Attempts++;
        copy.LastAttemptAt = now;
        copy.LastError = category.Length > 200 ? category[..200] : category;
        // 1, 2, 4 … minutes, at most an hour.
        copy.NextAttemptAt = now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, copy.Attempts - 1))));
        if (copy.RemoteId is null && copy.Alert is { } alert && now - alert.CreatedAt > GiveUpAfter)
        {
            copy.State = StaffAlertCopyStates.Failed;
            copy.Done = true;
        }
        _logger.LogWarning("Portal copy for alert {AlertId} will be retried ({Category}, attempt {Attempt}).",
            copy.AlertId, copy.LastError, copy.Attempts);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Could not record the retry for alert {AlertId}.", copy.AlertId);
            _db.ChangeTracker.Clear();
        }
    }

    private async Task<bool> NudgeAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var since = now - NudgeWindow;
        var fresh = await _db.StaffAlerts.Where(a => a.NudgedAt == null).OrderBy(a => a.Id).Take(500).ToListAsync(cancellationToken);
        var refresh = StaffAlertSignal.TakeRefreshRequest();
        if (fresh.Count == 0 && !refresh) return false;

        var needed = refresh || fresh.Any(a => a.CreatedAt >= since);
        if (needed)
        {
            try
            {
                await _gateway.NudgeAsync(cancellationToken);
            }
            catch (StaffAlertGatewayException ex)
            {
                _logger.LogWarning("POS staff refresh notice failed ({Category}); bells will catch up by polling.", ex.Category);
                if (refresh) StaffAlertSignal.RequestRefresh();
                return false;
            }
        }

        // Older ones are past being "live": bells have long since polled them.
        foreach (var alert in fresh) alert.NudgedAt = now;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Could not record the staff refresh notice.");
            _db.ChangeTracker.Clear();
        }
        return needed;
    }

    public static string IdempotencyKey(StaffAlert alert) => $"{alert.Kind}.{alert.OccurrenceKey.Replace(':', '-')}";

    private static TimeSpan PollInterval(TimeSpan age) =>
        age < TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(1)
        : age < TimeSpan.FromDays(1) ? TimeSpan.FromMinutes(5)
        : TimeSpan.FromMinutes(30);
}

/// <summary>Runs the dispatcher when a commit wakes it, and every 30 seconds regardless.</summary>
public sealed class StaffAlertDispatcherBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly StaffAlertOptions _options;
    private readonly ILogger<StaffAlertDispatcherBackgroundService> _logger;

    public StaffAlertDispatcherBackgroundService(IServiceProvider services, StaffAlertOptions options, ILogger<StaffAlertDispatcherBackgroundService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SendsEnabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<StaffAlertDispatcher>();
                var summary = await dispatcher.RunOnceAsync(stoppingToken);
                if (summary.CopiesSent > 0 || summary.AudiencesResolved > 0)
                    _logger.LogInformation("Staff alerts: {Resolved} audience(s) resolved, {Sent} Portal copy(ies) sent, {Synced} synced.",
                        summary.AudiencesResolved, summary.CopiesSent, summary.CopiesSynced);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staff alert delivery run failed.");
            }

            try
            {
                // A wake (new commit) runs sooner; a short pause lets a burst of commits coalesce.
                if (await StaffAlertSignal.WaitAsync(Interval, stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
