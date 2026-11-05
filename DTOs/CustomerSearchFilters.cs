using System.Text.Json.Serialization;
using DataBridge.Models;

namespace DataBridge.DTOs;

// A null or blank filter is not applied.
public class CustomerSearchFilters
{
    public string? Location { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CustomerStatus? Status { get; set; }

    public string? Company { get; set; }
    public string? Name { get; set; }
}
