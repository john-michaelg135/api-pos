using Api.Contracts.Location;

namespace Applications.Interfaces;

public interface ILocationService
{
    Task<LocationResponseDto> CreateLocationAsync(CreateLocationDto dto);
    Task<List<LocationResponseDto>> GetAllLocationsAsync();
    Task<LocationResponseDto?> UpdateLocationAsync(int locationId, UpdateLocationDto dto);

    // ── User → location assignments ──
    Task<List<UserLocationResponseDto>> GetAllUserLocationsAsync();
    Task<List<UserLocationResponseDto>> GetUserLocationsAsync(string authUserId);
    Task<UserLocationResponseDto> AssignUserLocationAsync(AssignUserLocationDto dto);
    Task<bool> UnassignUserLocationAsync(int id);
}
