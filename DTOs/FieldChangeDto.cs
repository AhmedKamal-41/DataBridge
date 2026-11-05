namespace DataBridge.DTOs;

// One field that differs between an existing customer and an incoming row.
public class FieldChangeDto
{
    public string FieldName { get; set; } = string.Empty;
    public string? CurrentValue { get; set; }
    public string? IncomingValue { get; set; }
}
