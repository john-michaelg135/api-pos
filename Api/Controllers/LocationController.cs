using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Api.Contracts.Location;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/locations")]
public class LocationController : ControllerBase
{
    private readonly ILocationService _locationService;

    public LocationController(ILocationService locationService)
    {
        _locationService = locationService;
    }

    // GET api-pos/locations
    [HttpGet]
    [ProducesResponseType(typeof(List<LocationResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllLocations()
    {
        var locations = await _locationService.GetAllLocationsAsync();
        return Ok(locations);
    }

    // POST api-pos/locations
    [HttpPost]
    [ProducesResponseType(typeof(LocationResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateLocation([FromBody] CreateLocationDto dto)
    {
        if (dto == null) return BadRequest("Location data is required.");
        if (string.IsNullOrWhiteSpace(dto.LocationName)) return BadRequest("Location name is required.");

        var location = await _locationService.CreateLocationAsync(dto);
        return CreatedAtAction(nameof(GetAllLocations), null, location);
    }

    // PUT api-pos/locations/{locationId}
    [HttpPut("{locationId:int}")]
    [ProducesResponseType(typeof(LocationResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateLocation(int locationId, [FromBody] UpdateLocationDto dto)
    {
        if (dto == null) return BadRequest("Update data is required.");

        var location = await _locationService.UpdateLocationAsync(locationId, dto);
        if (location == null) return NotFound("Location not found.");
        return Ok(location);
    }

    // ── User → location assignments ──

    // GET api-pos/locations/user-locations
    [HttpGet("user-locations")]
    [ProducesResponseType(typeof(List<UserLocationResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllUserLocations()
    {
        var assignments = await _locationService.GetAllUserLocationsAsync();
        return Ok(assignments);
    }

    // GET api-pos/locations/user-locations/{authUserId}
    [HttpGet("user-locations/{authUserId}")]
    [ProducesResponseType(typeof(List<UserLocationResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserLocations(string authUserId)
    {
        if (string.IsNullOrWhiteSpace(authUserId)) return BadRequest("A user id is required.");
        var assignments = await _locationService.GetUserLocationsAsync(authUserId);
        return Ok(assignments);
    }

    // POST api-pos/locations/user-locations
    [HttpPost("user-locations")]
    [ProducesResponseType(typeof(UserLocationResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AssignUserLocation([FromBody] AssignUserLocationDto dto)
    {
        if (dto == null) return BadRequest("Assignment data is required.");
        if (string.IsNullOrWhiteSpace(dto.AuthUserId)) return BadRequest("A user id is required.");
        if (dto.LocationId <= 0) return BadRequest("A valid locationId is required.");

        try
        {
            var assignment = await _locationService.AssignUserLocationAsync(dto);
            return CreatedAtAction(nameof(GetUserLocations), new { authUserId = dto.AuthUserId }, assignment);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // DELETE api-pos/locations/user-locations/{id}
    [HttpDelete("user-locations/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnassignUserLocation(int id)
    {
        var removed = await _locationService.UnassignUserLocationAsync(id);
        if (!removed) return NotFound("Assignment not found.");
        return NoContent();
    }
}
