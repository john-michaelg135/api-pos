namespace Api.Contracts.Location;

public class AssignUserLocationDto
{
    public string AuthUserId { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>br-auth id of the admin performing the assignment (audit).</summary>
    public string? AssignedBy { get; set; }
}
