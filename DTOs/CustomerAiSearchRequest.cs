using System.ComponentModel.DataAnnotations;

namespace DataBridge.DTOs;

public class CustomerAiSearchRequest
{
    [Required]
    [MaxLength(500)]
    public string? Question { get; set; }
}
