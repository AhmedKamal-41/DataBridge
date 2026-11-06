namespace DataBridge.Models;

// One recorded action. The service must set at least one of the three IDs.
public class ImportAuditEntry
{
    public int Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }

    // What changed or why, such as a rejection reason. Null if there's nothing to add.
    public string? Details { get; set; }

    public int? ImportBatchId { get; set; }
    public ImportBatch? Batch { get; set; }
    public int? ImportRowId { get; set; }
    public int? CustomerId { get; set; }
}
