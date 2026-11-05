namespace DataBridge.DTOs;

// One page of results. T is the item type, such as ImportRowDto.
public class PagedResponse<T>
{
    public List<T> Items { get; set; } = new List<T>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}
