namespace DataBridge.DTOs;

public class ImportJobDto
{
    // "Parse" or "Validate".
    public string Kind { get; set; } = string.Empty;

    // "Queued", "Parsing", "Saving", "Validating", "Completed" or "Failed".
    public string Stage { get; set; } = string.Empty;

    // True while the job is queued or running, so a page knows to keep polling.
    public bool IsActive { get; set; }

    // Null until the row count is known.
    public int? ProcessedRows { get; set; }
    public int? TotalRows { get; set; }

    public string? Error { get; set; }
}
