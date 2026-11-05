namespace DataBridge.DTOs;

public class ImportAuditEntryDto
{
    public int Id { get; set; }
    public string Action { get; set; } = string.Empty;

    // The signed-in user's ID, or "system" for a background job.
    public string Actor { get; set; } = string.Empty;

    // The user's email, for display. Null when Actor isn't a user.
    public string? ActorName { get; set; }

    public DateTime TimestampUtc { get; set; }
    public int? ImportRowId { get; set; }
    public int? CustomerId { get; set; }
    public string? Details { get; set; }
}
