namespace Domains.Entities;

public class CartItem
{
    public int CartItemId { get; set; }

    // External customer ID — stored as string to match CRM ID format (e.g. "cust_001")
    // No FK constraint since customer data lives in CRM/Auth service
    public string CustomerId { get; set; } = string.Empty;

    // FK to ProductVariation (what product variant is in the cart)
    public int VariationId { get; set; }

    public int Quantity { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Navigation
    public ProductVariation? Variation { get; set; }
}
