namespace DataBridge.Models;

// One uploaded file.
public class ImportBatch
{
    public int Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string FileName { get; set; } = string.Empty;

    // JSON array of the headers, in file order. Empty until the file is parsed.
    public string OriginalHeadersJson { get; set; } = string.Empty;

    // JSON that maps a field to a column position, such as {"Name":0,"Email":2}.
    // Null until the columns are mapped.
    public string? ColumnMappingJson { get; set; }

    public ImportBatchStatus Status { get; set; } = ImportBatchStatus.Pending;

    public DateTime UploadedAtUtc { get; set; }
    public DateTime? ReadyForReviewAtUtc { get; set; }
    public DateTime? CommittedAtUtc { get; set; }

    // Commit summary. These stay null until the batch is committed.
    public int? CustomersCreated { get; set; }
    public int? CustomersUpdated { get; set; }
    public int? RowsSkipped { get; set; }

    // SQL Server changes this on every update, so a stale save can be detected.
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<ImportRow> Rows { get; set; } = new List<ImportRow>();

    public ImportBatchJob Job { get; set; } = null!;
    public ImportBatchFile? File { get; set; }
}
