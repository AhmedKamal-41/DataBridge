namespace DataBridge.DTOs;

public class CustomerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Location { get; set; }

    // Readable text, such as "Active".
    public string Status { get; set; } = string.Empty;

    public string? Notes { get; set; }
}
