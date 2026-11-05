using System.ComponentModel.DataAnnotations;

namespace DataBridge.DTOs;

// Each value is a zero-based column position, so 0 is the first column.
public class SaveColumnMappingRequest
{
    // These are int? so that a missing value stays null and fails [Required].
    // A plain int would turn a missing value into 0, which is a valid column.
    [Required]
    [Range(0, int.MaxValue)]
    public int? NameColumn { get; set; }

    [Required]
    [Range(0, int.MaxValue)]
    public int? EmailColumn { get; set; }

    [Required]
    [Range(0, int.MaxValue)]
    public int? StatusColumn { get; set; }

    [Range(0, int.MaxValue)]
    public int? CompanyColumn { get; set; }

    [Range(0, int.MaxValue)]
    public int? PhoneColumn { get; set; }

    [Range(0, int.MaxValue)]
    public int? LocationColumn { get; set; }

    [Range(0, int.MaxValue)]
    public int? NotesColumn { get; set; }
}
