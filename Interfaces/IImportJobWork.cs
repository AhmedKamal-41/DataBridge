namespace DataBridge.Interfaces;

// The work behind a background job. ImportJobRunner handles Hangfire, locking and
// retries, and calls this for everything that involves import rules.
public interface IImportJobWork
{
    // Returns null when the batch has no queued or running job.
    Task<int?> GetActiveRunNumberAsync(int batchId);

    Task ProcessAsync(int batchId, int runNumber, CancellationToken cancellationToken);

    // Null message clears a note left by an earlier attempt.
    Task NoteRetryAsync(int batchId, int runNumber, string message);

    Task MarkFailedAsync(int batchId, int runNumber, string message);
}
