using System.Text.Json;
using DataBridge.Data;
using DataBridge.Models;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Services;

// Opt-in cleanup of the raw files from finished member imports. It's off by default.
public class ImportFileRetentionService
{
    public const int DefaultDays = 90;

    private readonly DataBridgeDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ImportFileRetentionService> _logger;

    public ImportFileRetentionService(DataBridgeDbContext db, IConfiguration configuration, ILogger<ImportFileRetentionService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task DeleteExpiredFilesAsync()
    {
        if (!_configuration.GetValue("Import:RawFileRetention:Enabled", false))
        {
            return;
        }

        int days = Math.Max(0, _configuration.GetValue("Import:RawFileRetention:Days", DefaultDays));
        DateTime cutoff = DateTime.UtcNow.AddDays(-days);

        // A job replays from the stored file, so only a committed batch with no active
        // job can lose it. Guest files go with their workspace.
        List<int> batchIds = await _db.ImportBatchFiles
            .Where(file => file.Batch.WorkspaceId == Guid.Empty
                && file.Batch.Status == ImportBatchStatus.Committed
                && file.Batch.CommittedAtUtc <= cutoff
                && (file.Batch.Job.Stage == ImportJobStage.Completed || file.Batch.Job.Stage == ImportJobStage.Failed))
            .Select(file => file.ImportBatchId)
            .ToListAsync();

        foreach (int batchId in batchIds)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            await _db.ImportBatchFiles.Where(file => file.ImportBatchId == batchId).ExecuteDeleteAsync();

            _db.ImportAuditEntries.Add(new ImportAuditEntry
            {
                Action = "RawFileDeleted",
                Actor = ImportService.SystemActor,
                TimestampUtc = DateTime.UtcNow,
                ImportBatchId = batchId,
                Details = JsonSerializer.Serialize(new { retentionDays = days })
            });
            await _db.SaveChangesAsync();

            await transaction.CommitAsync();
        }

        _logger.LogInformation("Raw file retention removed {Count} file(s).", batchIds.Count);
    }
}
