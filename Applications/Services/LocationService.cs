using System.Globalization;
using Api.Middlewares;
using Applications.Audit;
using Applications.Interfaces;
using Api.Contracts.Location;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class LocationService : ILocationService
{
    private readonly PosDbContext _db;
    private readonly IAuditLogService _audit;

    public LocationService(PosDbContext db, IAuditLogService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<LocationResponseDto> CreateLocationAsync(CreateLocationDto dto)
    {
        var location = new Location
        {
            LocationName = dto.LocationName,
            LocationType = dto.LocationType,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        await _db.Locations.AddAsync(location);
        await _db.SaveChangesAsync();

        await RecordAsync(AuditActions.Created, LocationTarget(location),
            who => $"{who} created location {location.LocationName}.",
            location.CreatedAt, $"location-created:{location.LocationId}",
            new() { ["operation"] = "location_created", ["location_id"] = location.LocationId });

        return MapToResponse(location);
    }

    public async Task<List<LocationResponseDto>> GetAllLocationsAsync()
    {
        var locations = await _db.Locations
            .AsNoTracking()
            .OrderBy(l => l.LocationName)
            .ToListAsync();

        return locations.Select(MapToResponse).ToList();
    }

    public async Task<LocationResponseDto?> UpdateLocationAsync(int locationId, UpdateLocationDto dto)
    {
        var location = await _db.Locations.FindAsync(locationId);
        if (location == null) return null;

        var changed = new List<string>();
        if (dto.LocationName != null && dto.LocationName != location.LocationName) { location.LocationName = dto.LocationName; changed.Add("name"); }
        if (dto.LocationType != null && !Equals(dto.LocationType, location.LocationType)) { location.LocationType = dto.LocationType; changed.Add("type"); }
        if (dto.IsActive.HasValue && dto.IsActive.Value != location.IsActive) { location.IsActive = dto.IsActive.Value; changed.Add("active"); }

        _db.Locations.Update(location);
        await _db.SaveChangesAsync();

        // An edit has no saved revision, so each submission is its own occurrence.
        if (changed.Count > 0)
        {
            await RecordAsync(AuditActions.Updated, LocationTarget(location),
                who => $"{who} edited location {location.LocationName} ({string.Join(", ", changed)}).",
                DateTime.UtcNow, operationId: null,
                new() { ["operation"] = "location_updated", ["location_id"] = location.LocationId, ["changed_fields"] = changed });
        }

        return MapToResponse(location);
    }

    // ── User → location assignments ──

    public async Task<List<UserLocationResponseDto>> GetAllUserLocationsAsync()
    {
        var assignments = await _db.PosUserLocations
            .AsNoTracking()
            .Include(ul => ul.Location)
            .OrderBy(ul => ul.AuthUserId)
            .ToListAsync();

        return assignments.Select(MapToUserLocationResponse).ToList();
    }

    public async Task<List<UserLocationResponseDto>> GetUserLocationsAsync(string authUserId)
    {
        var assignments = await _db.PosUserLocations
            .AsNoTracking()
            .Include(ul => ul.Location)
            .Where(ul => ul.AuthUserId == authUserId)
            .ToListAsync();

        return assignments.Select(MapToUserLocationResponse).ToList();
    }

    public async Task<LocationResponseDto?> GetLocationForAssignmentAsync(int locationId)
    {
        var location = await _db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.LocationId == locationId && item.IsActive);

        return location is null ? null : MapToResponse(location);
    }

    public async Task<UserLocationResponseDto> AssignUserLocationAsync(AssignUserLocationDto dto, string? staffDisplayName = null)
    {
        var location = await _db.Locations.FindAsync(dto.LocationId)
            ?? throw new InvalidOperationException("Location not found.");

        var userAssignments = await _db.PosUserLocations
            .Where(ul => ul.AuthUserId == dto.AuthUserId)
            .ToListAsync();
        var existing = await _db.PosUserLocations
            .FirstOrDefaultAsync(ul => ul.AuthUserId == dto.AuthUserId && ul.LocationId == dto.LocationId);

        var staffName = await StaffNameAsync(dto.AuthUserId, staffDisplayName);
        if (existing != null)
        {
            var others = userAssignments.Where(assignment => assignment.Id != existing.Id).ToList();
            var primaryChanged = existing.IsPrimary != dto.IsPrimary;
            _db.PosUserLocations.RemoveRange(others);
            existing.IsPrimary = dto.IsPrimary;
            await _db.SaveChangesAsync();
            existing.Location = location;

            // Re-saving the same assignment changes nothing and records nothing.
            if (others.Count > 0 || primaryChanged)
            {
                await RecordAssignmentAsync(AuditActions.Updated, dto.AuthUserId, staffName, location,
                    "assignment_changed", $"assigned {staffName} to {location.LocationName} only",
                    operationId: null, previousLocationId: others.FirstOrDefault()?.LocationId);
            }
            return MapToUserLocationResponse(existing);
        }

        var previous = userAssignments.FirstOrDefault();
        _db.PosUserLocations.RemoveRange(userAssignments);
        var assignment = new PosUserLocation
        {
            AuthUserId = dto.AuthUserId,
            LocationId = dto.LocationId,
            IsPrimary = dto.IsPrimary,
            AssignedBy = dto.AssignedBy,
            AssignedAt = DateTime.UtcNow
        };

        await _db.PosUserLocations.AddAsync(assignment);
        await _db.SaveChangesAsync();

        assignment.Location = location;
        if (previous is null)
        {
            await RecordAssignmentAsync(AuditActions.Created, dto.AuthUserId, staffName, location,
                "assignment_added", $"assigned {staffName} to {location.LocationName}",
                $"assignment:{assignment.Id}", previousLocationId: null);
        }
        else
        {
            await RecordAssignmentAsync(AuditActions.Updated, dto.AuthUserId, staffName, location,
                "assignment_changed", $"moved {staffName} to {location.LocationName}",
                $"assignment:{assignment.Id}", previous.LocationId);
        }
        return MapToUserLocationResponse(assignment);
    }

    public async Task<bool> UnassignUserLocationAsync(int id)
    {
        var assignment = await _db.PosUserLocations.Include(ul => ul.Location).FirstOrDefaultAsync(ul => ul.Id == id);
        if (assignment == null) return false;

        // Capture who and where before the row is gone.
        var authUserId = assignment.AuthUserId;
        var location = assignment.Location;
        var locationId = assignment.LocationId;
        var staffName = await StaffNameAsync(authUserId, null);

        _db.PosUserLocations.Remove(assignment);
        await _db.SaveChangesAsync();

        var branch = location?.LocationName ?? $"Branch {locationId}";
        await _audit.RecordAsync(new AuditEvent
        {
            Module = PosModules.Locations,
            Action = AuditActions.Deleted,
            Target = AssignmentTarget(authUserId, staffName),
            Describe = who => $"{who} removed {staffName} from {branch}.",
            OccurredAt = DateTime.UtcNow,
            OperationId = $"assignment-removed:{id}",
            Metadata = new Dictionary<string, object?> { ["operation"] = "assignment_removed", ["location_id"] = locationId },
        });
        return true;
    }

    private Task RecordAsync(string action, AuditTarget target, Func<string, string> describe, DateTime occurredAt,
        string? operationId, Dictionary<string, object?> metadata) =>
        _audit.RecordAsync(new AuditEvent
        {
            Module = PosModules.Locations,
            Action = action,
            Target = target,
            Describe = describe,
            OccurredAt = occurredAt,
            OperationId = operationId,
            Metadata = metadata,
        });

    // The staff member's branch assignment, one per person, so add, change and removal share a target.
    private Task RecordAssignmentAsync(string action, string authUserId, string staffName, Location location,
        string operation, string what, string? operationId, int? previousLocationId) =>
        RecordAsync(action, AssignmentTarget(authUserId, staffName), who => $"{who} {what}.", DateTime.UtcNow, operationId,
            new()
            {
                ["operation"] = operation,
                ["location_id"] = location.LocationId,
                ["previous_location_id"] = previousLocationId,
            });

    private static AuditTarget LocationTarget(Location location) =>
        new("location", location.LocationId.ToString(CultureInfo.InvariantCulture), location.LocationName);

    private static AuditTarget AssignmentTarget(string authUserId, string staffName) =>
        new("staff_assignment", authUserId, $"{staffName} branch assignment");

    private async Task<string> StaffNameAsync(string authUserId, string? known)
    {
        if (!string.IsNullOrWhiteSpace(known)) return known.Trim();
        var seen = await _db.PosStaffUsers.AsNoTracking()
            .Where(u => u.AuthUserId == authUserId)
            .Select(u => new { u.DisplayName, u.Username })
            .FirstOrDefaultAsync();
        return !string.IsNullOrWhiteSpace(seen?.DisplayName) ? seen!.DisplayName!
            : !string.IsNullOrWhiteSpace(seen?.Username) ? seen!.Username
            : "a staff member";
    }

    private static LocationResponseDto MapToResponse(Location location)
    {
        return new LocationResponseDto
        {
            LocationId = location.LocationId,
            LocationName = location.LocationName,
            LocationType = location.LocationType,
            IsActive = location.IsActive,
            CreatedAt = location.CreatedAt
        };
    }

    private static UserLocationResponseDto MapToUserLocationResponse(PosUserLocation ul)
    {
        return new UserLocationResponseDto
        {
            Id = ul.Id,
            AuthUserId = ul.AuthUserId,
            LocationId = ul.LocationId,
            LocationName = ul.Location?.LocationName ?? string.Empty,
            LocationType = ul.Location?.LocationType ?? string.Empty,
            IsPrimary = ul.IsPrimary,
            AssignedAt = ul.AssignedAt
        };
    }
}
