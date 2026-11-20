using Applications.StaffAlerts;
using Domains.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Infrastructures.Persistence;

/// <summary>
/// Staff bell alerts are saved in the same commit as the business change that raised them:
/// services stage an intent with <see cref="RaiseStaffAlert"/>, and stock rows that enter a
/// low-stock episode raise one by themselves. After the business rows are written (so a status
/// history row has its ID) the alerts are inserted in the same transaction; a duplicate occurrence
/// is skipped under a savepoint, so a replay or a race never fails the business change.
/// </summary>
public partial class PosDbContext
{
    private const string AlertSavepoint = "staff_alert";
    private readonly List<StaffAlertIntent> _staffAlertIntents = [];
    private bool _insertingStaffAlerts;

    public DbSet<StaffAlert> StaffAlerts { get; set; }
    public DbSet<StaffAlertCopy> StaffAlertCopies { get; set; }

    /// <summary>Raise <paramref name="intent"/> with the next save; dropped if that save fails.</summary>
    public void RaiseStaffAlert(StaffAlertIntent intent) => _staffAlertIntents.Add(intent);

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (_insertingStaffAlerts) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        TrackLowStockEpisodes();
        if (_staffAlertIntents.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var intents = _staffAlertIntents.ToList();
        _staffAlertIntents.Clear();

        var relational = Database.IsRelational();
        IDbContextTransaction? owned = relational && Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var changed = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            var created = await InsertStaffAlertsAsync(intents, relational, cancellationToken);
            if (owned is not null) await owned.CommitAsync(cancellationToken);
            if (created > 0) StaffAlertSignal.Wake();
            return changed;
        }
        catch
        {
            if (owned is not null) await owned.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    /// <summary>
    /// Entry into <c>MinThreshold &gt; 0 &amp;&amp; Quantity &lt; MinThreshold</c> opens an episode and raises one
    /// alert; changes while low stay in it; recovering to the threshold closes it. Covers every
    /// stock write path (sales, refunds, voids, receiving, adjustments, threshold edits).
    /// </summary>
    private void TrackLowStockEpisodes()
    {
        foreach (var entry in ChangeTracker.Entries<Stock>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var stock = entry.Entity;
            var low = stock.MinThreshold > 0 && stock.Quantity < stock.MinThreshold;
            if (low && !stock.LowStockAlertOpen)
            {
                stock.LowStockAlertOpen = true;
                stock.LowStockEpisode++;
                _staffAlertIntents.Add(new StaffAlertIntent
                {
                    Kind = StaffAlertKinds.LowStock,
                    OccurrenceKey = StaffAlertIntent.LowStockKey(stock.LocationId, stock.VariationId, stock.LowStockEpisode),
                    LocationId = stock.LocationId,
                    VariationId = stock.VariationId,
                });
            }
            else if (!low && stock.LowStockAlertOpen)
            {
                stock.LowStockAlertOpen = false;
            }
        }
    }

    private async Task<int> InsertStaffAlertsAsync(List<StaffAlertIntent> intents, bool relational, CancellationToken cancellationToken)
    {
        _insertingStaffAlerts = true;
        try
        {
            var locationIds = intents.Select(i => i.LocationId).OfType<int>().Distinct().ToArray();
            var branches = await Locations.AsNoTracking()
                .Where(l => locationIds.Contains(l.LocationId))
                .ToDictionaryAsync(l => l.LocationId, l => l.LocationName, cancellationToken);
            var variationIds = intents.Select(i => i.VariationId).OfType<int>().Distinct().ToArray();
            var items = variationIds.Length == 0
                ? new Dictionary<int, string>()
                : await ProductVariations.AsNoTracking()
                    .Where(v => variationIds.Contains(v.VariationId))
                    .Select(v => new { v.VariationId, v.VariationName, ProductName = v.Product != null ? v.Product.ProductName : null })
                    .ToDictionaryAsync(v => v.VariationId, v => StaffAlertText.ItemLabel(v.ProductName, v.VariationName), cancellationToken);

            var created = 0;
            var now = DateTime.UtcNow;
            foreach (var intent in intents)
            {
                var key = intent.ResolveKey();
                var text = StaffAlertText.For(
                    intent,
                    intent.LocationId is { } loc ? branches.GetValueOrDefault(loc) : null,
                    intent.VariationId is { } v ? items.GetValueOrDefault(v) : null);
                var decision = StaffAlertKinds.IsDecision(intent.Kind);
                var alert = new StaffAlert
                {
                    OccurrenceKey = key,
                    Kind = intent.Kind,
                    Module = StaffAlertKinds.ModuleFor(intent.Kind),
                    Audience = StaffAlertKinds.AudienceFor(intent.Kind),
                    LocationId = intent.LocationId,
                    OrderId = intent.OrderId,
                    TransferId = intent.TransferId,
                    VariationId = intent.VariationId,
                    SourceHistoryId = intent.History?.Id,
                    RecipientPortalUserId = decision ? intent.PersonPortalUserId : null,
                    RequesterPortalUserId = decision ? null : intent.PersonPortalUserId,
                    Partial = intent.Partial,
                    Title = text.Title,
                    Body = text.Body,
                    LinkPath = text.LinkPath,
                    CreatedAt = intent.History?.CreatedAt ?? now,
                };

                if (!relational)
                {
                    if (await StaffAlerts.AnyAsync(a => a.OccurrenceKey == key, cancellationToken)) continue;
                    StaffAlerts.Add(alert);
                    await base.SaveChangesAsync(true, cancellationToken);
                    created++;
                    continue;
                }

                var transaction = Database.CurrentTransaction!;
                await transaction.CreateSavepointAsync(AlertSavepoint, cancellationToken);
                StaffAlerts.Add(alert);
                try
                {
                    await base.SaveChangesAsync(true, cancellationToken);
                    await transaction.ReleaseSavepointAsync(AlertSavepoint, cancellationToken);
                    created++;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // Same occurrence already saved (replay or a concurrent writer): keep that one.
                    await transaction.RollbackToSavepointAsync(AlertSavepoint, cancellationToken);
                    Entry(alert).State = EntityState.Detached;
                }
            }
            return created;
        }
        finally
        {
            _insertingStaffAlerts = false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}
