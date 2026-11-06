namespace DataBridge.Models;

// The latest background job for a batch. Progress lives here, not on ImportBatch,
// so that a progress update doesn't change the batch's RowVersion. That version
// then changes only for real work, which is how a job detects a newer mapping.
public class ImportBatchJob
{
    public int ImportBatchId { get; set; }
    public ImportBatch Batch { get; set; } = null!;

    public ImportJobKind Kind { get; set; }
    public ImportJobStage Stage { get; set; }

    // Goes up each time new work replaces the old, so an older job can't overwrite
    // the progress of the work that replaced it.
    public int RunNumber { get; set; }

    public int? ProcessedRows { get; set; }
    public int? TotalRows { get; set; }
    public string? Error { get; set; }

    // Null until Hangfire has the job. Recovery looks for this after a crash.
    public DateTime? EnqueuedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public bool IsActive => Stage != ImportJobStage.Completed && Stage != ImportJobStage.Failed;
}
