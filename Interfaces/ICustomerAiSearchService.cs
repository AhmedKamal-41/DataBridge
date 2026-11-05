using DataBridge.DTOs;

namespace DataBridge.Interfaces;

public interface ICustomerAiSearchService
{
    Task<CustomerAiSearchResultDto> SearchAsync(string question);
}
