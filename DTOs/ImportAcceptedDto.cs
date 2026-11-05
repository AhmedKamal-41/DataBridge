namespace DataBridge.DTOs;

// The 202 response: the work is saved and queued, and StatusUrl shows its progress.
public class ImportAcceptedDto
{
    public int BatchId { get; set; }
    public string StatusUrl { get; set; } = string.Empty;
    public ImportJobDto Job { get; set; } = new ImportJobDto();
}
