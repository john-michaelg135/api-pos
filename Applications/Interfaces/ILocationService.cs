using Api.Contracts.Location;

namespace Applications.Interfaces;

public interface ILocationService
{
    Task<LocationResponseDto> CreateLocationAsync(CreateLocationDto dto);
    Task<List<LocationResponseDto>> GetAllLocationsAsync();
    Task<LocationResponseDto?> UpdateLocationAsync(int locationId, UpdateLocationDto dto);
}
