using System.ComponentModel.DataAnnotations;

namespace DataBridge.DTOs;

// The controller binds this from the query string, such as ?page=2&pageSize=50.
public class PageRequest
{
    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 25;
}
