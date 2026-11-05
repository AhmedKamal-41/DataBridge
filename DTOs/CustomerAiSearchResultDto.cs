namespace DataBridge.DTOs;

public class CustomerAiSearchResultDto
{
    // "Applied": the filters ran. "Unsupported": the question isn't a customer search.
    // "NoFilters": it had nothing to filter on. Only "Applied" returns customers.
    public string Outcome { get; set; } = string.Empty;

    public string? Message { get; set; }
    public CustomerSearchFilters? Filters { get; set; }
    public PagedResponse<CustomerDto>? Customers { get; set; }

    // The most customers one AI search returns. Customers.TotalCount can be higher.
    public int MaxResults { get; set; }
}
