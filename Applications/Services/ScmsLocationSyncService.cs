using Api.Contracts.Scms;
using Infrastructures.Externals;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

/// <summary>
/// Keeps SCMS's copy of POS's branches current: every location (stores, bazaars and the
/// Commissary), switched-off ones included with isActive = false, so SCMS also learns
/// about closures. Sent after each branch change and from the "Sync branches to SCMS" button.
/// </summary>
public class ScmsLocationSyncService
{
    private readonly PosDbContext _db;
    private readonly ScmsApiClient _scms;
    private readonly ILogger<ScmsLocationSyncService> _logger;

    public ScmsLocationSyncService(PosDbContext db, ScmsApiClient scms, ILogger<ScmsLocationSyncService> logger)
    {
        _db = db;
        _scms = scms;
        _logger = logger;
    }

    public async Task<ScmsLocationSyncResult> PushAllAsync(CancellationToken cancellationToken = default)
    {
        var locations = await _db.Locations
            .AsNoTracking()
            .OrderBy(l => l.LocationId)
            .Select(l => new ScmsLocationItem
            {
                LocationId = l.LocationId,
                LocationName = l.LocationName,
                Address = string.Empty,
                IsActive = l.IsActive,
            })
            .ToListAsync(cancellationToken);

        var failure = await _scms.SyncLocationsAsync(new ScmsLocationSyncRequest { Locations = locations }, cancellationToken);
        if (failure is not null)
        {
            _logger.LogWarning("Branch sync to SCMS failed: {Reason}", failure);
            return new ScmsLocationSyncResult { Success = false, LocationCount = locations.Count, Message = failure };
        }

        return new ScmsLocationSyncResult
        {
            Success = true,
            LocationCount = locations.Count,
            Message = $"Sent {locations.Count} locations to SCMS.",
        };
    }
}
