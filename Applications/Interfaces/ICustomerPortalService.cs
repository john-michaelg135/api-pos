using Api.Contracts.Customer;
using Domains.Entities;

namespace Applications.Interfaces;

public interface ICustomerPortalService
{
    Task<List<CustomerOrderHistoryDto>> GetOrderHistoryAsync(int customerId);
    Task<OrderTrackingDto?> GetOrderTrackingAsync(int orderId, int customerId);
}
