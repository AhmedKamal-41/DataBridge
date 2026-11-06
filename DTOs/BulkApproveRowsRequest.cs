using System.ComponentModel.DataAnnotations;

namespace DataBridge.DTOs;

public class BulkApproveRowsRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(500)]
    public List<BulkApproveRowItem> Rows { get; set; } = new List<BulkApproveRowItem>();
}

public class BulkApproveRowItem
{
    public int RowId { get; set; }

    [Required]
    public byte[]? RowVersion { get; set; }
}
