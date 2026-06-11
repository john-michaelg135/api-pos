using Api.Contracts.Shared;
using Api.Contracts.OrderEntry;

namespace Api.Contracts.OrderManagement;

public class OrderManagementResponseDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string OrderSource { get; set; } = string.Empty;
    public string OrderStatus { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string? AppliedVoucherCode { get; set; }
    public decimal? VoucherDiscountAmount { get; set; }
    public int? CustomerId { get; set; }
    public int? LocationId { get; set; }
    public string? LocationName { get; set; }
    public string? DeliveryAddress { get; set; }

    // Institutional address fields (US-POS-008)
    public string? InstitutionalStreet { get; set; }
    public string? InstitutionalCity { get; set; }
    public string? InstitutionalProvince { get; set; }
    public string? InstitutionalZipCode { get; set; }

    public string? ContactPerson { get; set; }
    public bool IsPreorder { get; set; }
    public string? CustomVariationNotes { get; set; }
    public int? SubmittedBy { get; set; }
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectionRemarks { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<PaymentResponseDto> Payments { get; set; } = new();
    public List<OrderItemResponseDto> Items { get; set; } = new();
}
