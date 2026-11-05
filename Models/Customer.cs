namespace DataBridge.Models;

// The committed record.
public class Customer
{
    // Shared by the database configuration and import validation, so the two
    // can't disagree.
    public const int NameMaxLength = 200;
    public const int CompanyMaxLength = 200;
    public const int EmailMaxLength = 320;
    public const int PhoneMaxLength = 50;
    public const int LocationMaxLength = 200;
    public const int NotesMaxLength = 2000;

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Location { get; set; }
    public CustomerStatus Status { get; set; }
    public string? Notes { get; set; }

    // SQL Server changes this on every update, so a stale save can be detected.
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
