using Api.Contracts.Customer;
using Domains.Entities;

namespace Applications.Interfaces;

public interface ICustomerPortalService
{
    Task<List<CustomerOrderHistoryDto>> GetOrderHistoryAsync(int customerId);
    Task<List<CustomerOrderHistoryDto>> GetOrderHistoryByAuthIdAsync(string customerAuthId);
    Task<OrderTrackingDto?> GetOrderTrackingAsync(int orderId, int customerId);
    Task<OrderTrackingDto?> GetOrderTrackingByAuthIdAsync(int orderId, string customerAuthId);
}
