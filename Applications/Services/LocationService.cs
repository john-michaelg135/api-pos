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
}
