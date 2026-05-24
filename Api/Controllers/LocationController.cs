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
}
