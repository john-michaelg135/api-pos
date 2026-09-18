namespace Api.Contracts.Location;

public class UserLocationResponseDto
{
    public int Id { get; set; }
    public string AuthUserId { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string LocationType { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public DateTime AssignedAt { get; set; }
}
