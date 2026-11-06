using System.ComponentModel.DataAnnotations;

namespace DataBridge.DTOs;

public class SaveRowDecisionRequest
{
    // "Approved" or "Rejected".
    [Required]
    public string? Decision { get; set; }

    // Required when rejecting a row.
    [MaxLength(500)]
    public string? Reason { get; set; }

    // Approving a Conflict overwrites a customer, so the reviewer must say so.
    public bool ConfirmUpdate { get; set; }

    // Required when approving a Conflict: matchedCustomerVersion from the row
    // preview, which proves which version of the customer the reviewer saw.
    public byte[]? CustomerVersion { get; set; }

    // The rowVersion from the row preview. Required to approve. If it's sent when
    // rejecting, a row that has changed since the preview is refused as well.
    public byte[]? RowVersion { get; set; }
}
