namespace Api.Contracts.Location;

public class CreateLocationDto
{
    public string LocationName { get; set; } = string.Empty;
    public string LocationType { get; set; } = "Store";  // Store or Bazaar
}
