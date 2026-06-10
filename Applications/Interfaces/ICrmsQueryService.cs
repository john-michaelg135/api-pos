using Api.Contracts.Crms;

namespace Applications.Interfaces;

public interface ICrmsQueryService
{
    // CRMS-POS-001: Customer order history with full filtering support
    Task<CrmsCustomerOrdersResponseDto> GetCustomerOrdersAsync(string customerId, CrmsOrderFilterDto filter);

    // CRMS-POS-002: Full details of a single order including all line items
    Task<CrmsOrderDetailDto?> GetOrderDetailAsync(int orderId);

    // CRMS-POS-003: Customer's current cart contents with computed totals
    Task<CrmsCartDto?> GetCustomerCartAsync(string customerId);
}
