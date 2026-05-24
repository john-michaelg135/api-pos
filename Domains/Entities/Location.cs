namespace Domains.Entities;

public class Location
{
    public int LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string LocationType { get; set; } = "Store";  // Store or Bazaar
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    // Navigation
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
