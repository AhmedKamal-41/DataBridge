namespace DataBridge.DTOs;

public class ImportCommitSummaryDto
{
    public int BatchId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CommittedAtUtc { get; set; }
    public int CustomersCreated { get; set; }
    public int CustomersUpdated { get; set; }
    public int RowsRejected { get; set; }
}
