namespace Api.Contracts.Location;

public class UpdateLocationDto
{
    public string? LocationName { get; set; }
    public string? LocationType { get; set; }
    public bool? IsActive { get; set; }
}
