using System.ComponentModel.DataAnnotations;

namespace Api.Contracts.OrderEntry;

public class CartItemDto
{
    public int VariationId { get; set; }
    
    [Range(1, 10000)]
    public int Quantity { get; set; }
}
