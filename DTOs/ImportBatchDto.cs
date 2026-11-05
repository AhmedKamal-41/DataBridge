namespace DataBridge.DTOs;

public class ImportBatchDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;

    // "Pending", "Uploaded", "ReadyForReview" or "Committed".
    public string Status { get; set; } = string.Empty;

    public int RowCount { get; set; }

    public ImportJobDto Job { get; set; } = new ImportJobDto();

    // The column names in file order. A header's position in this list is the
    // zero-based number to send in SaveColumnMappingRequest.
    public List<string> Headers { get; set; } = new List<string>();

    // Maps a field to a column position, such as {"Name":0,"Email":2}.
    // Null until the columns are mapped.
    public Dictionary<string, int>? Mapping { get; set; }

    // Null until the batch is committed.
    public ImportCommitSummaryDto? CommitSummary { get; set; }
}
