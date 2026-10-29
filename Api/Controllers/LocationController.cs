using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Applications.Interfaces;
using Applications.Services;
using Api.Middlewares;
using Api.Contracts.Location;

namespace Api.Controllers;

[ApiController]
[Route("api-pos/locations")]
public class LocationController : ControllerBase
{
    // Tells the browser whether the automatic branch push to SCMS worked ("ok" or "failed").
    private const string ScmsSyncHeader = "X-Scms-Sync";

    private readonly ILocationService _locationService;
    private readonly IPortalUserDirectory _portalUserDirectory;
    private readonly ScmsLocationSyncService _scmsLocationSync;

    public LocationController(
        ILocationService locationService,
        IPortalUserDirectory portalUserDirectory,
        ScmsLocationSyncService scmsLocationSync)
    {
        _locationService = locationService;
        _portalUserDirectory = portalUserDirectory;
        _scmsLocationSync = scmsLocationSync;
    }

    // After a branch is added or changed, send SCMS the updated list. The save has already
    // happened, so a failure here only sets the header; the "Sync branches" button retries.
    private async Task PushBranchesToScmsAsync()
    {
        var result = await _scmsLocationSync.PushAllAsync(HttpContext.RequestAborted);
        Response.Headers[ScmsSyncHeader] = result.Success ? "ok" : "failed";
    }

    // GET api-pos/locations
    [HttpGet]
    [ProducesResponseType(typeof(List<LocationResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllLocations()
    {
        var locations = await _locationService.GetAllLocationsAsync();
        var user = HttpContext.GetCurrentUser();
        if (user is { IsSuperUser: false })
        {
            locations = locations
                .Where(location => user.LocationIds.Contains(location.LocationId))
                .ToList();
        }

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
        await PushBranchesToScmsAsync();
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
        await PushBranchesToScmsAsync();
        return Ok(location);
    }

    // POST api-pos/locations/sync-to-scms
    // Sends every location to SCMS now (first sync, or a retry after a failed automatic push).
    [HttpPost("sync-to-scms")]
    [ProducesResponseType(typeof(Api.Contracts.Scms.ScmsLocationSyncResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Api.Contracts.Scms.ScmsLocationSyncResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SyncToScms()
    {
        if (PosRoleAuthorization.RequireAdministrator(HttpContext) is { } denied) return denied;

        var result = await _scmsLocationSync.PushAllAsync(HttpContext.RequestAborted);
        return result.Success ? Ok(result) : StatusCode(StatusCodes.Status502BadGateway, result);
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

    // GET api-pos/locations/staff
    [HttpGet("staff")]
    public async Task<IActionResult> GetStaff(CancellationToken cancellationToken)
    {
        IReadOnlyList<PortalPosUser> staff;
        try
        {
            staff = await _portalUserDirectory.ListEligiblePosUsersAsync(cancellationToken);
        }
        catch (PortalDirectoryNotConfiguredException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "The read-only Enterprise Portal staff directory is not configured for api-pos.",
            });
        }

        var assignments = await _locationService.GetAllUserLocationsAsync();
        var assignmentsByUser = assignments
            .GroupBy(assignment => assignment.AuthUserId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return Ok(new
        {
            users = staff.Select(user => new
            {
                authUserId = user.Id,
                user.Username,
                fullName = $"{user.FirstName} {user.LastName}".Trim(),
                user.Email,
                user.Role,
                locations = assignmentsByUser.GetValueOrDefault(user.Id) ?? [],
            }),
        });
    }

    // GET api-pos/locations/mine
    [HttpGet("mine")]
    public async Task<IActionResult> GetMyLocations()
    {
        var user = HttpContext.GetCurrentUser();
        if (user?.PortalUserId is not { Length: > 0 } userId)
        {
            return Unauthorized(new { message = "A valid Enterprise Portal user is required." });
        }

        if (user.IsSuperUser)
        {
            return Ok(new { scope = "all", locations = Array.Empty<UserLocationResponseDto>() });
        }

        var locations = await _locationService.GetUserLocationsAsync(userId);
        return Ok(new
        {
            scope = "assigned",
            locations = locations.Select(location => new
            {
                location.LocationId,
                location.LocationName,
                location.LocationType,
                location.IsPrimary,
            }),
        });
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

        var assignedBy = HttpContext.GetCurrentUser()?.PortalUserId;
        if (string.IsNullOrWhiteSpace(assignedBy))
        {
            return Unauthorized(new { message = "A valid Enterprise Portal user is required." });
        }

        try
        {
            var targetUser = await _portalUserDirectory.FindEligiblePosUserAsync(
                dto.AuthUserId,
                HttpContext.RequestAborted);
            if (targetUser is null)
            {
                return BadRequest(new { message = "Select an active user with POS access." });
            }

            var location = await _locationService.GetLocationForAssignmentAsync(dto.LocationId);
            if (location is null)
            {
                return BadRequest(new { message = "Select an active location." });
            }

            dto.AssignedBy = assignedBy;
            dto.IsPrimary = true;
            var assignment = await _locationService.AssignUserLocationAsync(dto);
            return CreatedAtAction(nameof(GetUserLocations), new { authUserId = dto.AuthUserId }, assignment);
        }
        catch (PortalDirectoryNotConfiguredException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "The read-only Enterprise Portal staff directory is not configured for api-pos.",
            });
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
