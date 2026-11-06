using DataBridge.Data;
using DataBridge.Interfaces;
using DataBridge.Models;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Services;

public class ImportJobQueue : IImportJobQueue
{
    // Longer than a normal gap between progress updates. If this fires too early,
    // the batch lock in ImportJobRunner stops two jobs running at once.
    private static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(5);

    private readonly DataBridgeDbContext _db;
    private readonly IBackgroundJobClient _jobClient;
    private readonly ILogger<ImportJobQueue> _logger;

    public ImportJobQueue(DataBridgeDbContext db, IBackgroundJobClient jobClient, ILogger<ImportJobQueue> logger)
    {
        _db = db;
        _jobClient = jobClient;
        _logger = logger;
    }

    public async Task EnqueueAsync(int batchId)
    {
        try
        {
            // Hangfire gets only the batch ID. The job reads everything else from
            // the database, so a replay always works from the current data.
            _jobClient.Enqueue<ImportJobRunner>(runner => runner.RunAsync(batchId, null!, CancellationToken.None));

            DateTime now = DateTime.UtcNow;
            await _db.ImportBatchJobs
                .IgnoreQueryFilters()
                .Where(job => job.ImportBatchId == batchId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.EnqueuedAtUtc, now));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Batch {BatchId} is saved but not queued. Recovery will queue it.", batchId);
        }
    }

    public async Task RecoverPendingWorkAsync()
    {
        DateTime stalledBefore = DateTime.UtcNow - StalledAfter;

        List<int> batchIds = await _db.ImportBatchJobs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(job => job.Stage != ImportJobStage.Completed && job.Stage != ImportJobStage.Failed)
            .Where(job => job.EnqueuedAtUtc == null || job.UpdatedAtUtc < stalledBefore)
            .Select(job => job.ImportBatchId)
            .ToListAsync();

        foreach (int batchId in batchIds)
        {
            _logger.LogWarning("Recovering pending work for batch {BatchId}.", batchId);
            await EnqueueAsync(batchId);
        }
    }
}
