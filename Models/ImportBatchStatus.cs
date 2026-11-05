namespace DataBridge.Models;

public enum ImportBatchStatus
{
    // The file is saved, but its rows have not been staged yet.
    Pending,
    Uploaded,
    ReadyForReview,
    Committed
}
