namespace DataBridge.DTOs;

// A PUT replaces all seven draft values, so a field left out becomes blank.
// Length and format are not checked here: validation saves those problems as
// issues on the row, so the reviewer can see them and correct them again.
public class CorrectImportRowRequest
{
    public string? DraftName { get; set; }
    public string? DraftCompany { get; set; }
    public string? DraftEmail { get; set; }
    public string? DraftPhone { get; set; }
    public string? DraftLocation { get; set; }
    public string? DraftStatus { get; set; }
    public string? DraftNotes { get; set; }
}
