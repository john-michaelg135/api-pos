using Applications.Interfaces;
using Api.Contracts.Location;
using Domains.Entities;
using Infrastructures.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Applications.Services;

public class LocationService : ILocationService
{
    private readonly PosDbContext _db;

    public LocationService(PosDbContext db)
    {
        _db = db;
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

        if (dto.LocationName != null) location.LocationName = dto.LocationName;
        if (dto.LocationType != null) location.LocationType = dto.LocationType;
        if (dto.IsActive.HasValue) location.IsActive = dto.IsActive.Value;

        _db.Locations.Update(location);
        await _db.SaveChangesAsync();

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

    public async Task<UserLocationResponseDto> AssignUserLocationAsync(AssignUserLocationDto dto)
    {
        var location = await _db.Locations.FindAsync(dto.LocationId)
            ?? throw new InvalidOperationException("Location not found.");

        // Idempotent: reuse an existing assignment for this user+location.
        var existing = await _db.PosUserLocations
            .FirstOrDefaultAsync(ul => ul.AuthUserId == dto.AuthUserId && ul.LocationId == dto.LocationId);

        if (existing != null)
        {
            existing.IsPrimary = dto.IsPrimary;
            _db.PosUserLocations.Update(existing);
            await _db.SaveChangesAsync();
            existing.Location = location;
            return MapToUserLocationResponse(existing);
        }

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
        return MapToUserLocationResponse(assignment);
    }

    public async Task<bool> UnassignUserLocationAsync(int id)
    {
        var assignment = await _db.PosUserLocations.FindAsync(id);
        if (assignment == null) return false;

        _db.PosUserLocations.Remove(assignment);
        await _db.SaveChangesAsync();
        return true;
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
