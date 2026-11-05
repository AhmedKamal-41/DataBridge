using DataBridge.DTOs;

namespace DataBridge.Interfaces;

public interface IImportService
{
    // Saves the file and a pending batch, then queues the parsing job.
    Task<ImportBatchDto> UploadAsync(string fileName, Stream fileStream);

    // Returns null when the batch does not exist.
    Task<ImportBatchDto?> GetBatchAsync(int batchId);

    Task<PagedResponse<ImportBatchDto>> GetBatchesAsync(int page, int pageSize);

    // Returns null when the batch does not exist.
    Task<PagedResponse<ImportRowDto>?> GetRowsAsync(int batchId, int page, int pageSize);

    // Returns null when the batch does not exist.
    Task<ImportBatchDto?> SaveMappingAsync(int batchId, SaveColumnMappingRequest request);

    // Queues the validation job. Returns null when the batch does not exist.
    Task<ImportBatchDto?> ValidateAsync(int batchId);

    // Returns null when the batch or the row does not exist.
    Task<ImportRowDto?> CorrectRowAsync(int batchId, int rowId, CorrectImportRowRequest request, string actorId);

    // Returns null when the batch or the row does not exist.
    Task<ImportRowDto?> SaveDecisionAsync(int batchId, int rowId, SaveRowDecisionRequest request, string actorId);

    // Returns null when the batch does not exist.
    Task<ImportCommitSummaryDto?> CommitAsync(int batchId, string actorId);

    // Returns null when the batch does not exist.
    Task<PagedResponse<ImportAuditEntryDto>?> GetAuditAsync(int batchId, int page, int pageSize);
}
