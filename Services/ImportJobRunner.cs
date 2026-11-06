using DataBridge.Data;
using DataBridge.Interfaces;
using Hangfire;
using Hangfire.Server;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Services;

// What Hangfire runs. A job can run more than once (after a crash, or from recovery),
// so it always checks the database for what still needs doing.
public class ImportJobRunner
{
    private const int MaxRetries = 3;

    private readonly DataBridgeDbContext _db;
    private readonly IImportJobWork _work;
    private readonly IBackgroundJobClient _jobClient;
    private readonly ILogger<ImportJobRunner> _logger;

    public ImportJobRunner(
        DataBridgeDbContext db,
        IImportJobWork work,
        IBackgroundJobClient jobClient,
        ILogger<ImportJobRunner> logger)
    {
        _db = db;
        _work = work;
        _jobClient = jobClient;
        _logger = logger;
    }

    // Hangfire fills in context and cancellationToken when the job runs.
    [AutomaticRetry(
        Attempts = MaxRetries,
        DelaysInSeconds = new[] { 10, 30, 60 },
        OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(int batchId, PerformContext context, CancellationToken cancellationToken)
    {
        Guid? workspaceId = await _db.ImportBatches.IgnoreQueryFilters()
            .Where(batch => batch.Id == batchId)
            .Select(batch => (Guid?)batch.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);
        if (workspaceId == null)
        {
            return;
        }

        _db.WorkspaceId = workspaceId.Value;
        await using SqlConnection lockConnection = await TryLockBatchAsync(batchId);

        if (lockConnection.State != System.Data.ConnectionState.Open)
        {
            // Another job has this batch. Check back later in case that job finishes
            // before seeing work that was queued after it started.
            _jobClient.Schedule<ImportJobRunner>(
                runner => runner.RunAsync(batchId, null!, CancellationToken.None),
                TimeSpan.FromSeconds(30));
            return;
        }

        int? runNumber = await _work.GetActiveRunNumberAsync(batchId);
        if (runNumber == null)
        {
            return;
        }

        try
        {
            await _work.ProcessAsync(batchId, runNumber.Value, cancellationToken);
        }
        catch (ImportException exception)
        {
            // Retrying can't fix a bad file, so this is the end of the job.
            await _work.MarkFailedAsync(batchId, runNumber.Value, exception.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A reviewer saved something newer while this job ran. Its save was
            // rolled back, which is what keeps the old results off the new data.
            _logger.LogInformation("Discarded older job results for batch {BatchId}.", batchId);
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down. Hangfire runs the job again after restart.
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Import job for batch {BatchId} failed.", batchId);

            _db.ChangeTracker.Clear();

            // These steps use the database too, which may be why the job failed. If they
            // fail as well, Hangfire must still see the original error and retry the job.
            try
            {
                int retryCount = context.GetJobParameter<int>("RetryCount");

                if (retryCount >= MaxRetries)
                {
                    await _work.MarkFailedAsync(batchId, runNumber.Value,
                        $"This job stopped after {MaxRetries + 1} attempts because of a problem on the server. Check the server log, then try again.");
                }
                else
                {
                    await _work.NoteRetryAsync(batchId, runNumber.Value,
                        "A temporary problem interrupted this job. It will retry automatically.");
                }
            }
            catch (Exception statusException)
            {
                _logger.LogWarning(statusException, "Could not update the job status for batch {BatchId}.", batchId);
            }

            throw;
        }
    }

    // SQL Server releases this lock if the process dies, so a crash can't leave a batch
    // locked. Returns a closed connection if another job holds the lock.
    private async Task<SqlConnection> TryLockBatchAsync(int batchId)
    {
        // Pooling is off because a pooled connection stays open after it is disposed,
        // and that would keep holding the lock.
        var connectionString = new SqlConnectionStringBuilder(_db.Database.GetConnectionString()) { Pooling = false };
        var connection = new SqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
            SELECT @result;
            """;
        command.Parameters.AddWithValue("@resource", $"DataBridge-import-batch-{batchId}");

        int result = (int)(await command.ExecuteScalarAsync())!;
        if (result < 0)
        {
            await connection.CloseAsync();
        }

        return connection;
    }
}
