namespace DataBridge.Interfaces;

public interface IImportJobQueue
{
    // Never throws for a queue problem: the work is already saved, so the recovery
    // job can queue it later.
    Task EnqueueAsync(int batchId);

    Task RecoverPendingWorkAsync();
}
