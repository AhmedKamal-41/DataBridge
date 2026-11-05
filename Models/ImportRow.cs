namespace DataBridge.Models;

// One spreadsheet row, held here until it is committed.
public class ImportRow
{
    public int Id { get; set; }

    public int ImportBatchId { get; set; }

    // "null!" tells the compiler that EF Core fills this in when it is loaded.
    public ImportBatch Batch { get; set; } = null!;

    public int SourceRowNumber { get; set; }

    // JSON array of the cells as uploaded. This never changes.
    public string OriginalCellsJson { get; set; } = string.Empty;

    // Editable text, so a reviewer can see and correct invalid values.
    public string? DraftName { get; set; }
    public string? DraftCompany { get; set; }
    public string? DraftEmail { get; set; }
    public string? DraftPhone { get; set; }
    public string? DraftLocation { get; set; }
    public string? DraftStatus { get; set; }
    public string? DraftNotes { get; set; }

    // True once a reviewer edits a draft value. Remapping is blocked after that,
    // because it would overwrite the reviewer's corrections.
    public bool IsCorrected { get; set; }

    public ReviewDecision ReviewDecision { get; set; } = ReviewDecision.Pending;

    // Why the reviewer rejected the row. Null otherwise.
    public string? DecisionReason { get; set; }

    // The customer version the reviewer saw when approving an update.
    public byte[]? ApprovedCustomerVersion { get; set; }

    // JSON of the values the reviewer approved. If the row no longer produces
    // the same JSON, the approval no longer applies.
    public string? ApprovedSnapshotJson { get; set; }

    // Null until matching has run.
    public MatchResult? MatchResult { get; set; }

    // Set for Duplicate and Conflict. Null for New.
    public int? MatchedCustomerId { get; set; }
    public Customer? MatchedCustomer { get; set; }

    public List<ImportIssue> Issues { get; set; } = new List<ImportIssue>();
}
