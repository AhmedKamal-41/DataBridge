namespace DataBridge.DTOs;

public class ImportRowDto
{
    public int Id { get; set; }
    public int SourceRowNumber { get; set; }
    public List<string> OriginalCells { get; set; } = new List<string>();

    public string? DraftName { get; set; }
    public string? DraftCompany { get; set; }
    public string? DraftEmail { get; set; }
    public string? DraftPhone { get; set; }
    public string? DraftLocation { get; set; }
    public string? DraftStatus { get; set; }
    public string? DraftNotes { get; set; }

    public bool IsCorrected { get; set; }
    public List<ImportIssueDto> Issues { get; set; } = new List<ImportIssueDto>();

    // "New", "Duplicate" or "Conflict". Null when the row has issues or the
    // batch has not been validated.
    public string? MatchResult { get; set; }

    // Set for Duplicate and Conflict.
    public int? MatchedCustomerId { get; set; }

    // Send this back in SaveRowDecisionRequest when approving a Conflict.
    public byte[]? MatchedCustomerVersion { get; set; }

    // Filled in only for Conflict. These are the exact changes an update makes.
    public List<FieldChangeDto> Changes { get; set; } = new List<FieldChangeDto>();

    // "Pending", "Approved" or "Rejected".
    public string ReviewDecision { get; set; } = string.Empty;
    public string? DecisionReason { get; set; }
}
