using DataBridge.DTOs;
using DataBridge.Interfaces;

namespace DataBridge.Services;

// Reads only. A question can select filters for the normal customer search, and it has
// no path to anything that writes to the database.
public class CustomerAiSearchService : ICustomerAiSearchService
{
    private const int MaxResults = 50;

    private readonly IOpenAiFilterClient _openAiClient;
    private readonly ICustomerService _customerService;

    public CustomerAiSearchService(IOpenAiFilterClient openAiClient, ICustomerService customerService)
    {
        _openAiClient = openAiClient;
        _customerService = customerService;
    }

    public async Task<CustomerAiSearchResultDto> SearchAsync(string question)
    {
        string? json = await _openAiClient.RequestFiltersJsonAsync(question);

        // Null means the model refused to answer.
        CustomerSearchFilters? filters = json == null ? null : CustomerAiFilterParser.Parse(json);

        if (filters == null)
        {
            return new CustomerAiSearchResultDto
            {
                Outcome = "Unsupported",
                Message = "AI search can only find customers by location, status, company, or name. Nothing was searched or changed.",
                MaxResults = MaxResults
            };
        }

        bool hasFilter = filters.Location != null || filters.Status != null || filters.Company != null || filters.Name != null;

        if (!hasFilter)
        {
            return new CustomerAiSearchResultDto
            {
                Outcome = "NoFilters",
                Message = "That question didn't include a location, status, company, or name to search for.",
                MaxResults = MaxResults
            };
        }

        return new CustomerAiSearchResultDto
        {
            Outcome = "Applied",
            Filters = filters,
            Customers = await _customerService.GetCustomersAsync(1, MaxResults, filters),
            MaxResults = MaxResults
        };
    }
}
