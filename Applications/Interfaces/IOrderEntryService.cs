using Api.Contracts.OrderEntry;

namespace Applications.Interfaces;

public interface IOrderEntryService
{
    // POS-005 / EC-004: Product grid (optional locationId adds stock info)
    Task<List<ProductGridItemDto>> GetProductGridAsync(int? locationId = null);

    // POS-006: Order creation and retrieval
    Task<OrderResponseDto> CreateOrderAsync(CreateOrderDto dto);
    Task<List<OrderResponseDto>> GetAllOrdersAsync();
    Task<OrderResponseDto?> GetOrderByIdAsync(int orderId);

    // POS-007: Walk-in order confirm
    Task<OrderResponseDto?> ConfirmOrderAsync(int orderId, ConfirmOrderDto dto);

    // POS-008: Institutional order creation
    Task<OrderResponseDto> CreateInstitutionalOrderAsync(CreateInstitutionalOrderDto dto);


    // EC-008: Ecommerce order submission
    Task<OrderResponseDto> CreateEcommerceOrderAsync(CreateEcommerceOrderDto dto);
}
