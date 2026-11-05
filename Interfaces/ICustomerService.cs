using DataBridge.DTOs;

namespace DataBridge.Interfaces;

public interface ICustomerService
{
    // The one customer query, used by both manual and AI search.
    Task<PagedResponse<CustomerDto>> GetCustomersAsync(int page, int pageSize, CustomerSearchFilters filters);
}
