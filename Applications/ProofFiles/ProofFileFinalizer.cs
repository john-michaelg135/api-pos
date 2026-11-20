using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Applications.ProofFiles;

public interface IProofFileFinalizer
{
    /// <summary>Runs one task as far as it will go now. Never throws for storage trouble; returns the stage reached.</summary>
    Task<string?> RunAsync(int taskId, CancellationToken cancellationToken = default);

    /// <summary>Runs up to <paramref name="max"/> open tasks whose retry time has come. Returns how many finished.</summary>
    Task<int> RunDueAsync(int max = 25, CancellationToken cancellationToken = default);
}

/// <summary>
/// Storage work owed after a committed proof photo. Every step is idempotent in file storage (adding
/// an existing Link, re-PATCHing retention, removing an absent Link all succeed), so a retry after a
/// lost answer, a restart, or two runners at once all converge. Before each step the task is checked
/// against the order's current POS row, so a newer refund photo always wins over recovery work for
/// an older one.
/// </summary>
public sealed class ProofFileFinalizer : IProofFileFinalizer
{
    private readonly PosDbContext _db;
    private readonly IFileStorage _storage;
    private readonly ILogger<ProofFileFinalizer> _logger;
    private readonly TimeProvider _clock;

    public ProofFileFinalizer(PosDbContext db, IFileStorage storage, ILogger<ProofFileFinalizer> logger, TimeProvider? clock = null)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string?> RunAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _db.ProofFileTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null) return null;
        await AdvanceAsync(task, cancellationToken);
        return task.Stage;
    }

    /// <summary>Once a replacement photo is attached, retire the links of the Files it replaced right away.</summary>
    private async Task RetireSupersededAsync(ProofFileTask attached, CancellationToken cancellationToken)
    {
        var superseded = await _db.ProofFileTasks
            .Where(t => t.OrderId == attached.OrderId && t.Kind == attached.Kind && t.Id != attached.Id && t.Stage == ProofFileTaskStages.Detach)
            .ToListAsync(cancellationToken);
        foreach (var old in superseded) await AdvanceAsync(old, cancellationToken);
    }

    public async Task<int> RunDueAsync(int max = 25, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var due = await _db.ProofFileTasks
            .Where(t => (t.Stage == ProofFileTaskStages.Pending || t.Stage == ProofFileTaskStages.Linked || t.Stage == ProofFileTaskStages.Detach)
                        && t.NextAttemptAt <= now)
            .OrderBy(t => t.NextAttemptAt)
            .Take(max)
            .ToListAsync(cancellationToken);

        var finished = 0;
        foreach (var task in due)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await AdvanceAsync(task, cancellationToken);
            if (!ProofFileTaskStages.IsOpen(task.Stage)) finished++;
        }
        return finished;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private async Task AdvanceAsync(ProofFileTask task, CancellationToken cancellationToken)
    {
        var link = ProofKinds.OrderLink(task.OrderId, task.Kind);
        try
        {
            while (ProofFileTaskStages.IsOpen(task.Stage))
            {
                if (ProofFileTaskStages.IsAttaching(task.Stage) && !await IsCurrentAsync(task, cancellationToken))
                {
                    // A newer photo became current; this File only needs its link retired.
                    await SaveStageAsync(task, ProofFileTaskStages.Detach, cancellationToken);
                    continue;
                }

                switch (task.Stage)
                {
                    case ProofFileTaskStages.Pending:
                        if (task.FileExpiresAt is { } expires && expires <= Now)
                        {
                            _logger.LogError("Proof File {FileId} for order {OrderId} ({Kind}) expired before it could be attached.",
                                task.FileId, task.OrderId, task.Kind);
                            await SaveStageAsync(task, ProofFileTaskStages.Expired, cancellationToken, completed: true);
                            return;
                        }
                        await _storage.AddLinkAsync(task.FileId, link, cancellationToken);
                        await SaveStageAsync(task, ProofFileTaskStages.Linked, cancellationToken);
                        break;

                    case ProofFileTaskStages.Linked:
                        var file = await _storage.SetRetentionAsync(task.FileId, FileStorageValues.Permanent, cancellationToken);
                        if (file is null || file.Id != task.FileId || file.RetentionType != FileStorageValues.Permanent || file.ExpiresAt is not null)
                            throw new ProofFinalizeException("promotion_not_verified");
                        await SaveStageAsync(task, ProofFileTaskStages.Done, cancellationToken, completed: true);
                        await RetireSupersededAsync(task, cancellationToken);
                        break;

                    case ProofFileTaskStages.Detach:
                        // Retire the old link only after the photo that replaced it is attached.
                        if (await _db.ProofFileTasks.AnyAsync(t => t.OrderId == task.OrderId && t.Kind == task.Kind && t.Id != task.Id &&
                                (t.Stage == ProofFileTaskStages.Pending || t.Stage == ProofFileTaskStages.Linked), cancellationToken))
                        {
                            await RetryLaterAsync(task, "waiting_for_replacement", cancellationToken, countAttempt: false);
                            return;
                        }
                        try
                        {
                            await _storage.RemoveLinkAsync(task.FileId, link, cancellationToken);
                        }
                        catch (FileStorageException ex) when (ex.Status == 404)
                        {
                            // Expired or removed in storage: no link is left to retire.
                        }
                        await SaveStageAsync(task, ProofFileTaskStages.Retired, cancellationToken, completed: true);
                        break;
                }
            }
        }
        catch (FileStorageException ex) when (ex.Status == 404 && ProofFileTaskStages.IsAttaching(task.Stage))
        {
            _logger.LogError("Proof File {FileId} for order {OrderId} ({Kind}) is gone from storage; it can't be attached.",
                task.FileId, task.OrderId, task.Kind);
            await SaveStageAsync(task, ProofFileTaskStages.Expired, cancellationToken, completed: true);
        }
        catch (FileStorageException ex)
        {
            var category = ex.Status is { } status ? $"storage_status_{status}" : "storage_unreachable";
            _logger.LogWarning("Proof File {FileId} for order {OrderId} ({Kind}) stays at {Stage}: {Category}. Will retry.",
                task.FileId, task.OrderId, task.Kind, task.Stage, category);
            await RetryLaterAsync(task, category, cancellationToken);
        }
        catch (ProofFinalizeException ex)
        {
            _logger.LogWarning("Proof File {FileId} for order {OrderId} ({Kind}) stays at {Stage}: {Category}. Will retry.",
                task.FileId, task.OrderId, task.Kind, task.Stage, ex.Message);
            await RetryLaterAsync(task, ex.Message, cancellationToken);
        }
    }

    /// <summary>Is this File still the one the order's POS row points at?</summary>
    private Task<bool> IsCurrentAsync(ProofFileTask task, CancellationToken cancellationToken) => task.Kind switch
    {
        ProofKinds.Delivery => _db.OrderDeliveryConfirmations.AnyAsync(c => c.OrderId == task.OrderId && c.ProofFileId == task.FileId, cancellationToken),
        ProofKinds.Refund => _db.OrderRefundProofs.AnyAsync(p => p.OrderId == task.OrderId && p.ProofFileId == task.FileId, cancellationToken),
        _ => Task.FromResult(false),
    };

    private async Task SaveStageAsync(ProofFileTask task, string stage, CancellationToken cancellationToken, bool completed = false)
    {
        var now = Now;
        task.Stage = stage;
        task.UpdatedAt = now;
        task.NextAttemptAt = now;
        task.LastError = null;
        if (completed) task.CompletedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RetryLaterAsync(ProofFileTask task, string category, CancellationToken cancellationToken, bool countAttempt = true)
    {
        var now = Now;
        if (countAttempt) task.Attempts++;
        // 1, 2, 4 … minutes, at most an hour; the temporary File lives seven days.
        var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Max(0, task.Attempts - 1))));
        task.NextAttemptAt = now.Add(delay);
        task.UpdatedAt = now;
        task.LastError = category.Length > 200 ? category[..200] : category;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Could not record the retry for proof File {FileId}.", task.FileId);
        }
    }

    private sealed class ProofFinalizeException(string category) : Exception(category);
}

/// <summary>Retries proof attachments that didn't finish during the request (gateway outage, restart).</summary>
public sealed class ProofFileFinalizerBackgroundService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly ProofStorageOptions _options;
    private readonly ILogger<ProofFileFinalizerBackgroundService> _logger;

    public ProofFileFinalizerBackgroundService(IServiceProvider services, ProofStorageOptions options, ILogger<ProofFileFinalizerBackgroundService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.HasGateway) return;
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var finalizer = scope.ServiceProvider.GetRequiredService<IProofFileFinalizer>();
                var finished = await finalizer.RunDueAsync(cancellationToken: stoppingToken);
                if (finished > 0) _logger.LogInformation("Finished {Count} pending proof photo attachment(s).", finished);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proof photo attachment sweep failed.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
