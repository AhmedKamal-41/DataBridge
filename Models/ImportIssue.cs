namespace DataBridge.Models;

// One validation problem on a row.
public class ImportIssue
{
    public int Id { get; set; }

    public int ImportRowId { get; set; }
    public ImportRow Row { get; set; } = null!;

    public string FieldName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
