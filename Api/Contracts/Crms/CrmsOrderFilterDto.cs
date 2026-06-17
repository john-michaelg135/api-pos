namespace Api.Contracts.Crms;

/// <summary>
/// Query parameters for GET /api-pos/customers/{customerId}/orders.
/// All fields are optional — omitting a field means no filter on that dimension.
/// </summary>
public class CrmsOrderFilterDto
{
    public string? OrderStatus { get; set; }
    public string? PaymentStatus { get; set; }
    public string? PaymentMethod { get; set; }

    /// <summary>Filter orders placed on or after this date.</summary>
    public DateTime? OrderedFrom { get; set; }

    /// <summary>Filter orders placed on or before this date.</summary>
    public DateTime? OrderedTo { get; set; }

    /// <summary>Filter by delivery/completion date range start.</summary>
    public DateTime? DeliveredFrom { get; set; }

    /// <summary>Filter by delivery/completion date range end.</summary>
    public DateTime? DeliveredTo { get; set; }

    public int? OrderId { get; set; }
    public int? ProductId { get; set; }

    /// <summary>Field to sort by: orderedAt | totalAmount | orderStatus. Defaults to orderedAt.</summary>
    public string? SortBy { get; set; }

    /// <summary>Sort direction: asc | desc. Defaults to desc.</summary>
    public string? SortOrder { get; set; }
}
