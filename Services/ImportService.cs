using System.Globalization;
using System.IO.Compression;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using DataBridge.Data;
using DataBridge.DTOs;
using DataBridge.Interfaces;
using DataBridge.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Services;

public class ImportService : IImportService, IImportJobWork
{
    private const long DefaultMaxUploadBytes = 5 * 1024 * 1024;
    private const int MaxFileNameLength = 255;
    private const int MaxJobErrorLength = 1000;

    // Each group of rows is saved on its own, so a replay after a crash only has
    // to save the rest.
    private const int RowSaveChunkSize = 500;

    // An .xlsx file is a zip, so a small upload can unpack into a huge workbook.
    private const long MaxUnpackedWorkbookBytes = 200 * 1024 * 1024;
    private const int MinPhoneDigits = 7;
    private const int MaxPhoneDigits = 15;
    private const string SystemActor = "system";

    private readonly DataBridgeDbContext _db;
    private readonly long _maxUploadBytes;
    private readonly IImportJobQueue _jobQueue;

    public ImportService(DataBridgeDbContext db, IConfiguration configuration, IImportJobQueue jobQueue)
    {
        _db = db;
        _jobQueue = jobQueue;
        _maxUploadBytes = configuration.GetValue("Import:MaxUploadBytes", DefaultMaxUploadBytes);
    }

    public async Task<ImportBatchDto> UploadAsync(string fileName, Stream fileStream)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension != ".csv" && extension != ".xlsx")
        {
            throw new ImportException(ImportErrorType.Unsupported, "Only .csv and .xlsx files can be imported.");
        }

        if (fileName.Length > MaxFileNameLength)
        {
            throw new ImportException(ImportErrorType.Invalid,
                $"The filename can have at most {MaxFileNameLength} characters.");
        }

        if (fileStream.Length > _maxUploadBytes)
        {
            throw new ImportException(ImportErrorType.TooLarge,
                $"The file is {fileStream.Length} bytes, but the limit is {_maxUploadBytes} bytes.");
        }

        using var fileCopy = new MemoryStream();
        await fileStream.CopyToAsync(fileCopy);

        DateTime now = DateTime.UtcNow;

        var batch = new ImportBatch
        {
            FileName = fileName,
            OriginalHeadersJson = "[]",
            Status = ImportBatchStatus.Pending,
            UploadedAtUtc = now,
            File = new ImportBatchFile { Content = fileCopy.ToArray() },
            Job = new ImportBatchJob
            {
                Kind = ImportJobKind.Parse,
                Stage = ImportJobStage.Queued,
                UpdatedAtUtc = now
            }
        };

        // One SaveChangesAsync runs in one transaction, so the file, batch and job
        // are all safe in SQL Server before anything is queued.
        _db.ImportBatches.Add(batch);
        await _db.SaveChangesAsync();

        await _jobQueue.EnqueueAsync(batch.Id);

        return ToBatchDto(batch, 0);
    }

    public async Task<ImportBatchDto?> GetBatchAsync(int batchId)
    {
        var result = await _db.ImportBatches
            .AsNoTracking()
            .Where(batch => batch.Id == batchId)
            .Select(batch => new { Batch = batch, Job = batch.Job, RowCount = batch.Rows.Count })
            .FirstOrDefaultAsync();

        if (result == null)
        {
            return null;
        }

        result.Batch.Job = result.Job;
        return ToBatchDto(result.Batch, result.RowCount);
    }

    public async Task<PagedResponse<ImportBatchDto>> GetBatchesAsync(int page, int pageSize)
    {
        int totalCount = await _db.ImportBatches.CountAsync();
        int totalPages = CalculateTotalPages(totalCount, pageSize);
        var items = new List<ImportBatchDto>();

        // Skipping the query past the last page also stops a huge page number
        // from overflowing the Skip calculation.
        if (page <= totalPages)
        {
            var results = await _db.ImportBatches
                .AsNoTracking()
                .OrderByDescending(batch => batch.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(batch => new { Batch = batch, Job = batch.Job, RowCount = batch.Rows.Count })
                .ToListAsync();

            foreach (var result in results)
            {
                result.Batch.Job = result.Job;
                items.Add(ToBatchDto(result.Batch, result.RowCount));
            }
        }

        return new PagedResponse<ImportBatchDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    public async Task<PagedResponse<ImportRowDto>?> GetRowsAsync(int batchId, int page, int pageSize)
    {
        bool batchExists = await _db.ImportBatches.AnyAsync(batch => batch.Id == batchId);
        if (!batchExists)
        {
            return null;
        }

        int totalCount = await _db.ImportRows.CountAsync(row => row.ImportBatchId == batchId);
        int totalPages = CalculateTotalPages(totalCount, pageSize);
        var items = new List<ImportRowDto>();

        if (page <= totalPages)
        {
            List<ImportRow> rows = await _db.ImportRows
                .AsNoTracking()
                .Include(row => row.Issues)
                .Where(row => row.ImportBatchId == batchId)
                .OrderBy(row => row.SourceRowNumber)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var customerIds = new List<int>();
            foreach (ImportRow row in rows)
            {
                if (row.MatchedCustomerId != null)
                {
                    customerIds.Add(row.MatchedCustomerId.Value);
                }
            }

            List<Customer> customers = await _db.Customers
                .AsNoTracking()
                .Where(customer => customerIds.Contains(customer.Id))
                .ToListAsync();

            foreach (ImportRow row in rows)
            {
                Customer? matchedCustomer = customers.FirstOrDefault(customer => customer.Id == row.MatchedCustomerId);
                items.Add(ToRowDto(row, matchedCustomer));
            }
        }

        return new PagedResponse<ImportRowDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    public async Task<ImportBatchDto?> SaveMappingAsync(int batchId, SaveColumnMappingRequest request)
    {
        ImportBatch? batch = await _db.ImportBatches
            .Include(b => b.Job)
            .Include(b => b.Rows)
            .ThenInclude(row => row.Issues)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch == null)
        {
            return null;
        }

        if (batch.Status == ImportBatchStatus.Pending)
        {
            throw new ImportException(ImportErrorType.Locked, GetNotParsedMessage(batch));
        }

        bool reviewHasStarted = batch.Rows.Any(row =>
            row.IsCorrected || row.ReviewDecision != ReviewDecision.Pending);

        if (batch.Status == ImportBatchStatus.Committed || reviewHasStarted)
        {
            throw new ImportException(ImportErrorType.Locked,
                "The columns can't be remapped after rows have been corrected or reviewed.");
        }

        int columnCount = ReadStringList(batch.OriginalHeadersJson).Count;
        Dictionary<string, int> mapping = BuildMapping(request, columnCount);

        batch.ColumnMappingJson = JsonSerializer.Serialize(mapping);

        // Validation results describe the old drafts, so the batch must be
        // validated again before review.
        batch.Status = ImportBatchStatus.Uploaded;
        batch.ReadyForReviewAtUtc = null;

        // This replaces any validation that is queued or running. If that job is
        // part way through, its save fails the batch's RowVersion check.
        batch.Job.RunNumber++;
        batch.Job.Stage = ImportJobStage.Completed;
        batch.Job.ProcessedRows = null;
        batch.Job.TotalRows = null;
        batch.Job.Error = null;
        batch.Job.UpdatedAtUtc = DateTime.UtcNow;

        foreach (ImportRow row in batch.Rows)
        {
            // Every draft is rewritten, so a field that is no longer mapped
            // can't keep a value from the previous mapping.
            List<string> cells = ReadStringList(row.OriginalCellsJson);
            row.DraftName = GetDraftValue(cells, mapping, "Name");
            row.DraftCompany = GetDraftValue(cells, mapping, "Company");
            row.DraftEmail = GetDraftValue(cells, mapping, "Email");
            row.DraftPhone = GetDraftValue(cells, mapping, "Phone");
            row.DraftLocation = GetDraftValue(cells, mapping, "Location");
            row.DraftStatus = GetDraftValue(cells, mapping, "Status");
            row.DraftNotes = GetDraftValue(cells, mapping, "Notes");

            row.Issues.Clear();
            row.MatchResult = null;
            row.MatchedCustomerId = null;
        }

        await SaveReviewChangesAsync(batch);

        return ToBatchDto(batch, batch.Rows.Count);
    }

    public async Task<ImportBatchDto?> ValidateAsync(int batchId)
    {
        ImportBatch? batch = await _db.ImportBatches
            .Include(b => b.Job)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch == null)
        {
            return null;
        }

        if (batch.Status == ImportBatchStatus.Committed)
        {
            throw new ImportException(ImportErrorType.Locked, "A committed batch can't be validated again.");
        }

        if (batch.Status == ImportBatchStatus.Pending)
        {
            throw new ImportException(ImportErrorType.Locked, GetNotParsedMessage(batch));
        }

        if (batch.ColumnMappingJson == null)
        {
            throw new ImportException(ImportErrorType.Invalid, "Save a column mapping before validating this batch.");
        }

        int rowCount = await _db.ImportRows.CountAsync(row => row.ImportBatchId == batchId);

        // A second click while validation is already queued or running changes nothing.
        if (batch.Job.IsActive)
        {
            return ToBatchDto(batch, rowCount);
        }

        // Review closes until the new results arrive, so nobody approves or commits
        // from results that are about to be replaced.
        batch.Status = ImportBatchStatus.Uploaded;
        batch.ReadyForReviewAtUtc = null;

        batch.Job.Kind = ImportJobKind.Validate;
        batch.Job.Stage = ImportJobStage.Queued;
        batch.Job.RunNumber++;
        batch.Job.ProcessedRows = 0;
        batch.Job.TotalRows = rowCount;
        batch.Job.Error = null;
        batch.Job.EnqueuedAtUtc = null;
        batch.Job.UpdatedAtUtc = DateTime.UtcNow;

        await SaveReviewChangesAsync(batch);

        await _jobQueue.EnqueueAsync(batch.Id);

        return ToBatchDto(batch, rowCount);
    }

    public async Task<int?> GetActiveRunNumberAsync(int batchId)
    {
        ImportBatchJob? job = await _db.ImportBatchJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.ImportBatchId == batchId);

        if (job == null || !job.IsActive)
        {
            return null;
        }

        return job.RunNumber;
    }

    public async Task ProcessAsync(int batchId, int runNumber, CancellationToken cancellationToken)
    {
        ImportBatchJob job = await _db.ImportBatchJobs
            .AsNoTracking()
            .FirstAsync(j => j.ImportBatchId == batchId);

        if (job.RunNumber != runNumber || !job.IsActive)
        {
            return;
        }

        if (job.Kind == ImportJobKind.Parse)
        {
            await ParseStoredFileAsync(batchId, runNumber, cancellationToken);
        }
        else
        {
            await ValidateStagedRowsAsync(batchId, runNumber);
        }
    }

    public Task NoteRetryAsync(int batchId, int runNumber, string message)
    {
        return _db.ImportBatchJobs
            .Where(job => job.ImportBatchId == batchId && job.RunNumber == runNumber)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Error, message)
                .SetProperty(job => job.UpdatedAtUtc, DateTime.UtcNow));
    }

    public Task MarkFailedAsync(int batchId, int runNumber, string message)
    {
        string error = message.Length <= MaxJobErrorLength ? message : message.Substring(0, MaxJobErrorLength);

        return _db.ImportBatchJobs
            .Where(job => job.ImportBatchId == batchId && job.RunNumber == runNumber)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Stage, ImportJobStage.Failed)
                .SetProperty(job => job.Error, error)
                .SetProperty(job => job.UpdatedAtUtc, DateTime.UtcNow));
    }

    // Safe to replay: the file doesn't change, and rows that an earlier attempt saved
    // are skipped. The unique index on batch and source row is the final guard.
    private async Task ParseStoredFileAsync(int batchId, int runNumber, CancellationToken cancellationToken)
    {
        await UpdateJobProgressAsync(batchId, runNumber, ImportJobStage.Parsing, null, null);

        ImportBatch storedBatch = await _db.ImportBatches.AsNoTracking().FirstAsync(batch => batch.Id == batchId);

        ImportBatchFile? file = await _db.ImportBatchFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.ImportBatchId == batchId);

        if (file == null)
        {
            throw new ImportException(ImportErrorType.Invalid, "The uploaded file is no longer stored. Upload it again.");
        }

        using var fileStream = new MemoryStream(file.Content);

        ParsedImportFile parsedFile;
        if (Path.GetExtension(storedBatch.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            parsedFile = ReadXlsx(storedBatch.FileName, fileStream);
        }
        else
        {
            parsedFile = await ReadCsvAsync(fileStream);
        }

        string headersJson = JsonSerializer.Serialize(parsedFile.Headers);
        await _db.ImportBatches
            .Where(batch => batch.Id == batchId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(batch => batch.OriginalHeadersJson, headersJson));

        List<int> savedRowNumbers = await _db.ImportRows
            .Where(row => row.ImportBatchId == batchId)
            .Select(row => row.SourceRowNumber)
            .ToListAsync();

        var alreadySaved = new HashSet<int>(savedRowNumbers);
        List<ImportRow> rowsToSave = parsedFile.Rows.Where(row => !alreadySaved.Contains(row.SourceRowNumber)).ToList();

        int totalRows = parsedFile.Rows.Count;
        int processedRows = totalRows - rowsToSave.Count;
        await UpdateJobProgressAsync(batchId, runNumber, ImportJobStage.Saving, processedRows, totalRows);

        foreach (ImportRow[] chunk in rowsToSave.Chunk(RowSaveChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (ImportRow row in chunk)
            {
                row.ImportBatchId = batchId;
            }

            _db.ImportRows.AddRange(chunk);
            await _db.SaveChangesAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();

            processedRows += chunk.Length;
            await UpdateJobProgressAsync(batchId, runNumber, ImportJobStage.Saving, processedRows, totalRows);
        }

        ImportBatch finishedBatch = await _db.ImportBatches
            .Include(batch => batch.Job)
            .FirstAsync(batch => batch.Id == batchId);

        finishedBatch.Status = ImportBatchStatus.Uploaded;
        finishedBatch.Job.Stage = ImportJobStage.Completed;
        finishedBatch.Job.Error = null;
        finishedBatch.Job.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task ValidateStagedRowsAsync(int batchId, int runNumber)
    {
        // One query, so the job, batch and rows are the current state together. The
        // RowVersion loaded here is what the final save checks.
        ImportBatch batch = await _db.ImportBatches
            .Include(b => b.Job)
            .Include(b => b.Rows)
            .ThenInclude(row => row.Issues)
            .AsSplitQuery()
            .FirstAsync(b => b.Id == batchId);

        if (batch.Job.RunNumber != runNumber || !batch.Job.IsActive)
        {
            return;
        }

        await UpdateJobProgressAsync(batchId, runNumber, ImportJobStage.Validating, 0, batch.Rows.Count);

        await RevalidateRowsAsync(batch, SystemActor);

        // All the results save in one transaction, so a crash can't leave half of them.
        await UpdateJobProgressAsync(batchId, runNumber, ImportJobStage.Saving, batch.Rows.Count, batch.Rows.Count);

        batch.Status = ImportBatchStatus.ReadyForReview;
        batch.ReadyForReviewAtUtc = DateTime.UtcNow;
        batch.Job.Stage = ImportJobStage.Completed;
        batch.Job.ProcessedRows = batch.Rows.Count;
        batch.Job.TotalRows = batch.Rows.Count;
        batch.Job.Error = null;
        batch.Job.UpdatedAtUtc = DateTime.UtcNow;
        AddAuditEntry("BatchValidated", SystemActor, batch.Id, null, null, null);

        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private Task UpdateJobProgressAsync(int batchId, int runNumber, ImportJobStage stage, int? processedRows, int? totalRows)
    {
        // Matching RunNumber stops an older job writing over newer progress.
        return _db.ImportBatchJobs
            .Where(job => job.ImportBatchId == batchId && job.RunNumber == runNumber)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Stage, stage)
                .SetProperty(job => job.ProcessedRows, processedRows)
                .SetProperty(job => job.TotalRows, totalRows)
                .SetProperty(job => job.UpdatedAtUtc, DateTime.UtcNow));
    }

    private static string GetNotParsedMessage(ImportBatch batch)
    {
        if (batch.Job.Stage == ImportJobStage.Failed)
        {
            return "This file could not be imported, so it has no rows to work with. Fix the file and upload it again.";
        }

        return "This file is still being imported. Wait for it to finish.";
    }

    public async Task<ImportRowDto?> CorrectRowAsync(int batchId, int rowId, CorrectImportRowRequest request, string actorId)
    {
        // Loads the whole batch because changing one row's email can start or
        // stop another row being a repeated email.
        ImportBatch? batch = await _db.ImportBatches
            .Include(b => b.Job)
            .Include(b => b.Rows)
            .ThenInclude(row => row.Issues)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        ImportRow? row = batch?.Rows.FirstOrDefault(r => r.Id == rowId);

        if (batch == null || row == null)
        {
            return null;
        }

        CheckBatchIsInReview(batch);

        string before = BuildRowSnapshot(row);

        row.DraftName = CleanValue(request.DraftName);
        row.DraftCompany = CleanValue(request.DraftCompany);
        row.DraftEmail = CleanValue(request.DraftEmail);
        row.DraftPhone = CleanValue(request.DraftPhone);
        row.DraftLocation = CleanValue(request.DraftLocation);
        row.DraftStatus = CleanValue(request.DraftStatus);
        row.DraftNotes = CleanValue(request.DraftNotes);
        row.IsCorrected = true;

        Dictionary<string, Customer> customersByEmail = await RevalidateRowsAsync(batch, actorId);

        AddAuditEntry("RowCorrected", actorId, batch.Id, row.Id, null,
            $"Before: {before} After: {BuildRowSnapshot(row)}");

        await SaveReviewChangesAsync(batch);

        return ToRowDto(row, FindMatchedCustomer(row, customersByEmail));
    }

    public async Task<ImportRowDto?> SaveDecisionAsync(int batchId, int rowId, SaveRowDecisionRequest request, string actorId)
    {
        ImportBatch? batch = await _db.ImportBatches
            .Include(b => b.Job)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch == null)
        {
            return null;
        }

        ImportRow? row = await _db.ImportRows
            .Include(r => r.Issues)
            .FirstOrDefaultAsync(r => r.Id == rowId && r.ImportBatchId == batchId);

        if (row == null)
        {
            return null;
        }

        CheckBatchIsInReview(batch);

        Customer? customer = await FindCustomerForRowAsync(row);

        if (request.Decision == nameof(ReviewDecision.Approved))
        {
            ApproveRow(row, customer, request);
            AddAuditEntry(row.MatchResult == MatchResult.Conflict ? "UpdateApproved" : "InsertApproved",
                actorId, batch.Id, row.Id, customer?.Id, null);
        }
        else if (request.Decision == nameof(ReviewDecision.Rejected))
        {
            string? reason = CleanValue(request.Reason);
            if (reason == null)
            {
                throw new ImportException(ImportErrorType.Invalid, "A reason is required to reject a row.");
            }

            row.ReviewDecision = ReviewDecision.Rejected;
            row.DecisionReason = reason;
            row.ApprovedCustomerVersion = null;
            row.ApprovedSnapshotJson = null;
            AddAuditEntry("RowRejected", actorId, batch.Id, row.Id, null, reason);
        }
        else
        {
            throw new ImportException(ImportErrorType.Invalid, "Decision must be Approved or Rejected.");
        }

        await SaveReviewChangesAsync(batch);

        return ToRowDto(row, customer);
    }

    public async Task<ImportCommitSummaryDto?> CommitAsync(int batchId, string actorId)
    {
        ImportBatch? batch = await _db.ImportBatches
            .Include(b => b.Job)
            .Include(b => b.Rows)
            .ThenInclude(row => row.Issues)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch == null)
        {
            return null;
        }

        if (batch.Status == ImportBatchStatus.Committed)
        {
            return ToCommitSummary(batch);
        }

        if (batch.Job.IsActive)
        {
            throw new ImportException(ImportErrorType.Locked, "This batch is still being processed. Wait for it to finish.");
        }

        if (batch.Status != ImportBatchStatus.ReadyForReview)
        {
            throw new ImportException(ImportErrorType.Invalid, "Validate this batch before committing it.");
        }

        List<ImportRow> rows = batch.Rows.OrderBy(row => row.SourceRowNumber).ToList();

        List<int> undecidedRows = rows
            .Where(row => row.ReviewDecision == ReviewDecision.Pending)
            .Select(row => row.SourceRowNumber)
            .ToList();

        if (undecidedRows.Count > 0)
        {
            throw new ImportException(ImportErrorType.Invalid,
                $"Every row needs a decision. Source rows without one: {string.Join(", ", undecidedRows)}.");
        }

        List<ImportRow> approvedRows = rows.Where(row => row.ReviewDecision == ReviewDecision.Approved).ToList();

        if (approvedRows.Count == 0)
        {
            throw new ImportException(ImportErrorType.Invalid, "At least one row must be approved.");
        }

        // These customers are tracked, so EF Core saves the changes made to them.
        Dictionary<string, Customer> customersByEmail = await LoadCustomersByEmailAsync(approvedRows, _db.Customers);

        List<int> staleRows = approvedRows
            .Where(row => !IsApprovalCurrent(row, FindMatchedCustomer(row, customersByEmail)))
            .Select(row => row.SourceRowNumber)
            .ToList();

        if (staleRows.Count > 0)
        {
            throw new ImportException(ImportErrorType.Locked,
                $"The approvals on source rows {string.Join(", ", staleRows)} are out of date because the customer data changed. Validate the batch again and review those rows.");
        }


        try
        {
            // New customers get their IDs from the first save, and the audit
            // entries in the second save need those IDs. The transaction makes
            // the two saves succeed or fail as one.
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var insertedCustomers = new Dictionary<ImportRow, Customer>();
            var updateDetails = new Dictionary<ImportRow, string>();

            foreach (ImportRow row in approvedRows)
            {
                if (row.MatchResult == MatchResult.New)
                {
                    var customer = new Customer
                    {
                        Name = row.DraftName!,
                        Company = row.DraftCompany,
                        Email = row.DraftEmail!,
                        NormalizedEmail = NormalizeEmail(row.DraftEmail!),
                        Phone = row.DraftPhone,
                        Location = row.DraftLocation,
                        Status = Enum.Parse<CustomerStatus>(row.DraftStatus!),
                        Notes = row.DraftNotes
                    };

                    _db.Customers.Add(customer);
                    insertedCustomers[row] = customer;
                }
                else
                {
                    // RowVersion is a concurrency token, so this update fails if
                    // another request changes the customer before it runs.
                    Customer customer = customersByEmail[NormalizeEmail(row.DraftEmail!)];
                    List<FieldChangeDto> changes = FindChanges(customer, row);
                    ApplyChanges(customer, changes);
                    updateDetails[row] = JsonSerializer.Serialize(changes);
                }
            }

            await _db.SaveChangesAsync();

            foreach (ImportRow row in approvedRows)
            {
                if (row.MatchResult == MatchResult.New)
                {
                    AddAuditEntry("CustomerInserted", actorId, batch.Id, row.Id, insertedCustomers[row].Id, null);
                }
                else
                {
                    AddAuditEntry("CustomerUpdated", actorId, batch.Id, row.Id, row.MatchedCustomerId, updateDetails[row]);
                }
            }

            batch.Status = ImportBatchStatus.Committed;
            batch.CommittedAtUtc = DateTime.UtcNow;
            batch.CustomersCreated = insertedCustomers.Count;
            batch.CustomersUpdated = updateDetails.Count;
            batch.RowsSkipped = rows.Count - approvedRows.Count;

            AddAuditEntry("BatchCommitted", actorId, batch.Id, null, null,
                $"{batch.CustomersCreated} inserted, {batch.CustomersUpdated} updated, {batch.RowsSkipped} rejected.");

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return await HandleCommitCollisionAsync(batchId);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // 2601 and 2627 are unique index errors: another request added a
            // customer with the same email after the check above.
            return await HandleCommitCollisionAsync(batchId);
        }

        return ToCommitSummary(batch);
    }

    public async Task<PagedResponse<ImportAuditEntryDto>?> GetAuditAsync(int batchId, int page, int pageSize)
    {
        bool batchExists = await _db.ImportBatches.AnyAsync(batch => batch.Id == batchId);
        if (!batchExists)
        {
            return null;
        }

        int totalCount = await _db.ImportAuditEntries.CountAsync(entry => entry.ImportBatchId == batchId);
        int totalPages = CalculateTotalPages(totalCount, pageSize);
        var items = new List<ImportAuditEntryDto>();

        if (page <= totalPages)
        {
            List<ImportAuditEntry> entries = await _db.ImportAuditEntries
                .AsNoTracking()
                .Where(entry => entry.ImportBatchId == batchId)
                .OrderBy(entry => entry.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            List<string> actorIds = entries.Select(entry => entry.Actor).Distinct().ToList();

            Dictionary<string, string?> actorNames = await _db.Users
                .AsNoTracking()
                .Where(user => actorIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.Email);

            foreach (ImportAuditEntry entry in entries)
            {
                items.Add(new ImportAuditEntryDto
                {
                    Id = entry.Id,
                    Action = entry.Action,
                    Actor = entry.Actor,
                    ActorName = actorNames.GetValueOrDefault(entry.Actor),
                    TimestampUtc = DateTime.SpecifyKind(entry.TimestampUtc, DateTimeKind.Utc),
                    ImportRowId = entry.ImportRowId,
                    CustomerId = entry.CustomerId,
                    Details = entry.Details
                });
            }
        }

        return new PagedResponse<ImportAuditEntryDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    // Validates every row, then withdraws any approval that no longer applies.
    // Returns the customers that matched, by normalized email.
    private async Task<Dictionary<string, Customer>> RevalidateRowsAsync(ImportBatch batch, string actor)
    {
        List<ImportRow> rows = batch.Rows.OrderBy(row => row.SourceRowNumber).ToList();
        Dictionary<string, List<int>> sourceRowsByEmail = GroupSourceRowsByEmail(rows);

        // Replacing the old results means running validation twice can't double them.
        foreach (ImportRow row in rows)
        {
            row.Issues.Clear();
            row.MatchResult = null;
            row.MatchedCustomerId = null;

            AddFieldIssues(row);

            string? email = CleanValue(row.DraftEmail);
            if (email != null)
            {
                List<int> sourceRows = sourceRowsByEmail[NormalizeEmail(email)];
                if (sourceRows.Count > 1)
                {
                    AddIssue(row, "Email",
                        $"This email appears on {sourceRows.Count} rows in this batch, first on source row {sourceRows[0]}.");
                }
            }
        }

        // AsNoTracking: validation only reads customers, so SaveChangesAsync
        // can't change them.
        Dictionary<string, Customer> customersByEmail =
            await LoadCustomersByEmailAsync(rows, _db.Customers.AsNoTracking());

        foreach (ImportRow row in rows)
        {
            if (row.Issues.Count > 0)
            {
                continue;
            }

            Customer? customer = customersByEmail.GetValueOrDefault(NormalizeEmail(row.DraftEmail!));
            row.MatchResult = ClassifyRow(row, customer);
            row.MatchedCustomerId = customer?.Id;
        }

        foreach (ImportRow row in rows)
        {
            if (row.ReviewDecision == ReviewDecision.Approved
                && !IsApprovalCurrent(row, FindMatchedCustomer(row, customersByEmail)))
            {
                row.ReviewDecision = ReviewDecision.Pending;
                row.ApprovedCustomerVersion = null;
                row.ApprovedSnapshotJson = null;
                AddAuditEntry("ApprovalCleared", actor, batch.Id, row.Id, null,
                    "The row or the matched customer changed after approval.");
            }
        }

        return customersByEmail;
    }

    private static void ApproveRow(ImportRow row, Customer? customer, SaveRowDecisionRequest request)
    {
        if (row.Issues.Count > 0)
        {
            throw new ImportException(ImportErrorType.Invalid,
                "A row with issues can't be approved. Correct it or reject it.");
        }

        // The customer data may have changed since the batch was validated.
        if (ClassifyRow(row, customer) != row.MatchResult)
        {
            throw new ImportException(ImportErrorType.Locked,
                "The customer data changed after validation. Validate the batch again.");
        }

        byte[]? approvedVersion = null;

        if (row.MatchResult == MatchResult.Duplicate)
        {
            throw new ImportException(ImportErrorType.Invalid,
                "This row matches the customer exactly, so there is nothing to approve. Reject it with a reason.");
        }

        if (row.MatchResult == MatchResult.Conflict)
        {
            if (!request.ConfirmUpdate)
            {
                throw new ImportException(ImportErrorType.Invalid,
                    "Approving a conflict updates the customer. Set confirmUpdate to true.");
            }

            if (request.CustomerVersion == null)
            {
                throw new ImportException(ImportErrorType.Invalid,
                    "Send customerVersion from the row preview to approve an update.");
            }

            if (!customer!.RowVersion.SequenceEqual(request.CustomerVersion))
            {
                throw new ImportException(ImportErrorType.Locked,
                    "The customer changed after you viewed it. Reload the row preview and review it again.");
            }

            approvedVersion = request.CustomerVersion;
        }

        row.ReviewDecision = ReviewDecision.Approved;
        row.DecisionReason = null;
        row.ApprovedCustomerVersion = approvedVersion;
        row.ApprovedSnapshotJson = BuildRowSnapshot(row);
    }

    // An approval applies only if the row still has the approved values and result,
    // and an approved update still targets the customer version the reviewer saw.
    private static bool IsApprovalCurrent(ImportRow row, Customer? customer)
    {
        if (row.Issues.Count > 0 || BuildRowSnapshot(row) != row.ApprovedSnapshotJson)
        {
            return false;
        }

        if (ClassifyRow(row, customer) != row.MatchResult)
        {
            return false;
        }

        if (row.MatchResult == MatchResult.Conflict)
        {
            return row.ApprovedCustomerVersion != null
                && customer!.RowVersion.SequenceEqual(row.ApprovedCustomerVersion);
        }

        return true;
    }

    private static MatchResult ClassifyRow(ImportRow row, Customer? customer)
    {
        if (customer == null)
        {
            return MatchResult.New;
        }

        return FindChanges(customer, row).Count == 0 ? MatchResult.Duplicate : MatchResult.Conflict;
    }

    private static string BuildRowSnapshot(ImportRow row)
    {
        return JsonSerializer.Serialize(new
        {
            row.DraftName,
            row.DraftCompany,
            row.DraftEmail,
            row.DraftPhone,
            row.DraftLocation,
            row.DraftStatus,
            row.DraftNotes,
            MatchResult = row.MatchResult?.ToString(),
            row.MatchedCustomerId
        });
    }

    private async Task<Customer?> FindCustomerForRowAsync(ImportRow row)
    {
        string? email = CleanValue(row.DraftEmail);
        if (email == null)
        {
            return null;
        }

        string normalizedEmail = NormalizeEmail(email);

        return await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(customer => customer.NormalizedEmail == normalizedEmail);
    }

    private static Customer? FindMatchedCustomer(ImportRow row, Dictionary<string, Customer> customersByEmail)
    {
        string? email = CleanValue(row.DraftEmail);
        if (email == null)
        {
            return null;
        }

        return customersByEmail.GetValueOrDefault(NormalizeEmail(email));
    }

    private static void CheckBatchIsInReview(ImportBatch batch)
    {
        if (batch.Status == ImportBatchStatus.Committed)
        {
            throw new ImportException(ImportErrorType.Locked, "A committed batch can't be changed.");
        }

        if (batch.Job.IsActive)
        {
            throw new ImportException(ImportErrorType.Locked, "This batch is still being processed. Wait for it to finish.");
        }

        if (batch.Status != ImportBatchStatus.ReadyForReview)
        {
            throw new ImportException(ImportErrorType.Invalid, "Validate this batch before reviewing its rows.");
        }
    }

    private void AddAuditEntry(string action, string actor, int batchId, int? rowId, int? customerId, string? details)
    {
        _db.ImportAuditEntries.Add(new ImportAuditEntry
        {
            Action = action,
            Actor = actor,
            TimestampUtc = DateTime.UtcNow,
            ImportBatchId = batchId,
            ImportRowId = rowId,
            CustomerId = customerId,
            Details = details
        });
    }

    // Forces an UPDATE of the batch even when only its rows changed. That gives the
    // batch a new RowVersion, so a commit and a review change can't overlap: whichever
    // saves second fails its RowVersion check instead of working from old data.
    private async Task SaveReviewChangesAsync(ImportBatch batch)
    {
        _db.Entry(batch).Property(b => b.Status).IsModified = true;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ImportException(ImportErrorType.Locked,
                "The batch was changed by another request. Reload it and try again.");
        }
    }

    // The save failed because another request got there first. If that request
    // committed this batch, retrying a commit should get the same saved summary.
    private async Task<ImportCommitSummaryDto> HandleCommitCollisionAsync(int batchId)
    {
        _db.ChangeTracker.Clear();

        ImportBatch batch = await _db.ImportBatches.AsNoTracking().FirstAsync(b => b.Id == batchId);

        if (batch.Status == ImportBatchStatus.Committed)
        {
            return ToCommitSummary(batch);
        }

        throw new ImportException(ImportErrorType.Locked,
            "A customer or this batch changed during the commit, and nothing was saved. Validate the batch again and review it.");
    }

    private static ImportCommitSummaryDto ToCommitSummary(ImportBatch batch)
    {
        return new ImportCommitSummaryDto
        {
            BatchId = batch.Id,
            Status = batch.Status.ToString(),

            // The database doesn't store that this value is UTC.
            CommittedAtUtc = DateTime.SpecifyKind(batch.CommittedAtUtc!.Value, DateTimeKind.Utc),
            CustomersCreated = batch.CustomersCreated!.Value,
            CustomersUpdated = batch.CustomersUpdated!.Value,
            RowsRejected = batch.RowsSkipped!.Value
        };
    }

    private static Dictionary<string, List<int>> GroupSourceRowsByEmail(List<ImportRow> rows)
    {
        var sourceRowsByEmail = new Dictionary<string, List<int>>();

        foreach (ImportRow row in rows)
        {
            string? email = CleanValue(row.DraftEmail);
            if (email == null)
            {
                continue;
            }

            string normalizedEmail = NormalizeEmail(email);
            if (!sourceRowsByEmail.ContainsKey(normalizedEmail))
            {
                sourceRowsByEmail[normalizedEmail] = new List<int>();
            }

            sourceRowsByEmail[normalizedEmail].Add(row.SourceRowNumber);
        }

        return sourceRowsByEmail;
    }

    // Messages never repeat the draft value: it can be any length, and
    // ImportIssue.Message has a maximum length.
    private static void AddFieldIssues(ImportRow row)
    {
        string? name = CleanValue(row.DraftName);
        if (name == null)
        {
            AddIssue(row, "Name", "Name is required.");
        }

        string? email = CleanValue(row.DraftEmail);
        if (email == null)
        {
            AddIssue(row, "Email", "Email is required.");
        }
        else if (!IsValidEmail(email))
        {
            AddIssue(row, "Email", "Email is not a valid email address.");
        }

        string? phone = CleanValue(row.DraftPhone);
        if (phone != null && !IsValidPhone(phone))
        {
            AddIssue(row, "Phone",
                $"Phone can use only digits, spaces, a leading +, and ( ) . - and must have {MinPhoneDigits} to {MaxPhoneDigits} digits.");
        }

        string? status = CleanValue(row.DraftStatus);
        if (status != nameof(CustomerStatus.Active) && status != nameof(CustomerStatus.Inactive))
        {
            AddIssue(row, "Status", "Status must be Active or Inactive.");
        }

        CheckMaxLength(row, "Name", name, Customer.NameMaxLength);
        CheckMaxLength(row, "Company", CleanValue(row.DraftCompany), Customer.CompanyMaxLength);
        CheckMaxLength(row, "Email", email, Customer.EmailMaxLength);
        CheckMaxLength(row, "Phone", phone, Customer.PhoneMaxLength);
        CheckMaxLength(row, "Location", CleanValue(row.DraftLocation), Customer.LocationMaxLength);
        CheckMaxLength(row, "Notes", CleanValue(row.DraftNotes), Customer.NotesMaxLength);
    }

    private static void CheckMaxLength(ImportRow row, string fieldName, string? value, int maxLength)
    {
        if (value != null && value.Length > maxLength)
        {
            AddIssue(row, fieldName, $"{fieldName} can have at most {maxLength} characters.");
        }
    }

    private static void AddIssue(ImportRow row, string fieldName, string message)
    {
        row.Issues.Add(new ImportIssue { FieldName = fieldName, Message = message });
    }

    private static bool IsValidEmail(string email)
    {
        if (!MailAddress.TryCreate(email, out MailAddress? address))
        {
            return false;
        }

        // MailAddress also accepts "Ana <ana@example.com>" and "ana@localhost".
        string host = address.Host;
        return address.Address == email
            && host.Contains('.')
            && !host.StartsWith('.')
            && !host.EndsWith('.');
    }

    private static bool IsValidPhone(string phone)
    {
        if (!Regex.IsMatch(phone, @"^\+?[0-9 ().\-]+$"))
        {
            return false;
        }

        int digitCount = phone.Count(char.IsAsciiDigit);
        return digitCount >= MinPhoneDigits && digitCount <= MaxPhoneDigits;
    }

    private static async Task<Dictionary<string, Customer>> LoadCustomersByEmailAsync(
        List<ImportRow> rows, IQueryable<Customer> customerQuery)
    {
        var emails = new List<string>();
        foreach (ImportRow row in rows)
        {
            if (row.Issues.Count == 0)
            {
                emails.Add(NormalizeEmail(CleanValue(row.DraftEmail)!));
            }
        }

        List<Customer> customers = await customerQuery
            .Where(customer => emails.Contains(customer.NormalizedEmail))
            .ToListAsync();

        // Ignores case to agree with SQL Server's comparison in the query above.
        var customersByEmail = new Dictionary<string, Customer>(StringComparer.OrdinalIgnoreCase);
        foreach (Customer customer in customers)
        {
            customersByEmail[customer.NormalizedEmail] = customer;
        }

        return customersByEmail;
    }

    // The one place that decides what an update changes. The conflict preview and
    // the commit both use it, so what the reviewer sees is what gets saved.
    private static List<FieldChangeDto> FindChanges(Customer customer, ImportRow row)
    {
        var fields = new List<(string Field, string? Current, string? Incoming)>
        {
            ("Name", customer.Name, row.DraftName),
            ("Company", customer.Company, row.DraftCompany),
            ("Phone", customer.Phone, row.DraftPhone),
            ("Location", customer.Location, row.DraftLocation),
            ("Status", customer.Status.ToString(), row.DraftStatus),
            ("Notes", customer.Notes, row.DraftNotes)
        };

        // Email is not compared here. The row matched this customer because the
        // normalized emails are equal, so the two emails are the same address.
        var changes = new List<FieldChangeDto>();

        foreach ((string field, string? current, string? incoming) in fields)
        {
            string? currentValue = CleanValue(current);
            string? incomingValue = CleanValue(incoming);

            // A blank or unmapped cell has a null draft, and it must never erase
            // what the customer already has.
            if (incomingValue != null && currentValue != incomingValue)
            {
                changes.Add(new FieldChangeDto
                {
                    FieldName = field,
                    CurrentValue = currentValue,
                    IncomingValue = incomingValue
                });
            }
        }

        return changes;
    }

    private static void ApplyChanges(Customer customer, List<FieldChangeDto> changes)
    {
        foreach (FieldChangeDto change in changes)
        {
            switch (change.FieldName)
            {
                case "Name":
                    customer.Name = change.IncomingValue!;
                    break;
                case "Company":
                    customer.Company = change.IncomingValue;
                    break;
                case "Phone":
                    customer.Phone = change.IncomingValue;
                    break;
                case "Location":
                    customer.Location = change.IncomingValue;
                    break;
                case "Status":
                    customer.Status = Enum.Parse<CustomerStatus>(change.IncomingValue!);
                    break;
                case "Notes":
                    customer.Notes = change.IncomingValue;
                    break;
            }
        }
    }

    private static string? CleanValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static async Task<ParsedImportFile> ReadCsvAsync(Stream fileStream)
    {
        var options = new CsvReaderOptions
        {
            // The headers are read as a normal row so that they can be validated
            // here, with clear errors, and stored exactly as uploaded.
            HasHeader = false,
            Separator = ',',
            TrackLineNumbers = true,
            TrimWhitespace = false,

            // By default, an unclosed quote silently swallows the rows after it.
            MalformedData = CsvMalformedDataHandling.Throw
        };

        List<string>? headers = null;
        var rows = new List<ImportRow>();

        try
        {
            using CsvReader reader = CsvReader.Create(fileStream, options, true);

            while (await reader.ReadAsync())
            {
                var cells = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    cells.Add(reader.GetString(i));
                }

                CheckCellsAreText(cells, reader.LineNumber);

                // CsvHelper skips only truly empty lines. "   " and ",,," also
                // arrive here, and they hold no data either.
                if (cells.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                if (headers == null)
                {
                    CheckHeaders(cells);
                    headers = cells;
                }
                else
                {
                    // The line in the file, so that a reviewer can find the row
                    // there. Blank lines and multi-line cells keep this accurate.
                    rows.Add(CreateStagedRow((int)reader.LineNumber, cells));
                }
            }
        }
        catch (CsvFormatException exception)
        {
            throw new ImportException(ImportErrorType.Invalid, exception.Message);
        }

        return CreateParsedFile(headers, rows);
    }

    private static ParsedImportFile ReadXlsx(string fileName, Stream fileStream)
    {
        List<string>? headers = null;
        var rows = new List<ImportRow>();

        try
        {
            CheckUnpackedWorkbookSize(fileStream);

            using var workbook = new XLWorkbook(fileStream);
            IXLWorksheet worksheet = workbook.Worksheet(1);

            if (worksheet.LastCellUsed() != null)
            {
                IXLCell? lastHeaderCell = worksheet.Row(1).LastCellUsed();
                if (lastHeaderCell == null)
                {
                    throw new ImportException(ImportErrorType.Invalid,
                        "Row 1 of the first worksheet must contain the headers, but it is blank.");
                }

                int columnCount = lastHeaderCell.Address.ColumnNumber;
                headers = ReadWorksheetCells(worksheet.Row(1), columnCount);
                CheckHeaders(headers);

                // RowsUsed leaves out empty rows, and RowNumber is still the real
                // row in the worksheet, so the reviewer can find it in Excel.
                foreach (IXLRow row in worksheet.RowsUsed())
                {
                    if (row.RowNumber() == 1)
                    {
                        continue;
                    }

                    List<string> cells = ReadWorksheetCells(row, columnCount);

                    if (!cells.All(string.IsNullOrWhiteSpace))
                    {
                        rows.Add(CreateStagedRow(row.RowNumber(), cells));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not ImportException)
        {
            // ClosedXML has no single error type for a bad file. A corrupt, encrypted
            // or renamed file can raise format, zip, XML or null reference errors.
            throw new ImportException(ImportErrorType.Unsupported,
                $"{fileName} can't be read as an Excel workbook. Open it in Excel, save it as .xlsx without a password, and upload it again.");
        }

        return CreateParsedFile(headers, rows);
    }

    private static void CheckUnpackedWorkbookSize(Stream fileStream)
    {
        long unpackedBytes = 0;

        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, true))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                unpackedBytes += entry.Length;
            }
        }

        fileStream.Position = 0;

        if (unpackedBytes > MaxUnpackedWorkbookBytes)
        {
            throw new ImportException(ImportErrorType.TooLarge,
                $"The workbook unpacks to {unpackedBytes} bytes, but the limit is {MaxUnpackedWorkbookBytes} bytes.");
        }
    }

    // Returns one string per column, so a row has at least as many cells as there
    // are headers, as in a CSV. Cells past the last header are kept as well.
    private static List<string> ReadWorksheetCells(IXLRow row, int columnCount)
    {
        int lastColumn = Math.Max(columnCount, row.LastCellUsed()?.Address.ColumnNumber ?? 0);
        var cells = new List<string>();

        for (int column = 1; column <= lastColumn; column++)
        {
            IXLCell cell = row.Cell(column);

            // Reading this as blank would silently lose whatever the formula calculates.
            if (cell.HasFormula && cell.CachedValue.IsBlank)
            {
                throw new ImportException(ImportErrorType.Invalid,
                    $"Worksheet row {row.RowNumber()}, column {cell.Address.ColumnLetter} has a formula with no saved result. Open the file in Excel, save it, and upload it again.");
            }

            // The text the cell shows in Excel, which is what a CSV export would
            // contain. This keeps the leading zeros in 00123, as text or as a format.
            cells.Add(cell.GetFormattedString(CultureInfo.InvariantCulture));
        }

        return cells;
    }

    private static ImportRow CreateStagedRow(int sourceRowNumber, List<string> cells)
    {
        return new ImportRow
        {
            SourceRowNumber = sourceRowNumber,
            OriginalCellsJson = JsonSerializer.Serialize(cells)
        };
    }

    private static ParsedImportFile CreateParsedFile(List<string>? headers, List<ImportRow> rows)
    {
        if (headers == null)
        {
            throw new ImportException(ImportErrorType.Invalid, "The file is empty. It needs a header row.");
        }

        if (rows.Count == 0)
        {
            throw new ImportException(ImportErrorType.Invalid, "The file has headers but no data rows.");
        }

        return new ParsedImportFile(headers, rows);
    }

    // A renamed image or a file in another encoding can still end in .csv. These
    // two characters show that its bytes could not be read as UTF-8 text.
    private static void CheckCellsAreText(List<string> cells, long lineNumber)
    {
        foreach (string cell in cells)
        {
            if (cell.Contains('\0') || cell.Contains('�'))
            {
                throw new ImportException(ImportErrorType.Unsupported,
                    $"Line {lineNumber} isn't valid text. Save the file as CSV UTF-8 and upload it again.");
            }
        }
    }

    private static void CheckHeaders(List<string> headers)
    {
        var firstColumnByHeader = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int column = 0; column < headers.Count; column++)
        {
            string header = headers[column].Trim();

            if (header.Length == 0)
            {
                throw new ImportException(ImportErrorType.Invalid,
                    $"The header in column {column} is missing.");
            }

            if (firstColumnByHeader.TryGetValue(header, out int firstColumn))
            {
                throw new ImportException(ImportErrorType.Invalid,
                    $"The header '{header}' is ambiguous. It appears in columns {firstColumn} and {column}.");
            }

            firstColumnByHeader[header] = column;
        }
    }

    private static Dictionary<string, int> BuildMapping(SaveColumnMappingRequest request, int columnCount)
    {
        // The controller already checks [Required], but the service also protects
        // this rule so that other callers, such as a Blazor page, can't skip it.
        if (request.NameColumn == null || request.EmailColumn == null || request.StatusColumn == null)
        {
            throw new ImportException(ImportErrorType.Invalid,
                "NameColumn, EmailColumn and StatusColumn are required.");
        }

        var selectedColumns = new List<(string Field, int? Column)>
        {
            ("Name", request.NameColumn),
            ("Company", request.CompanyColumn),
            ("Email", request.EmailColumn),
            ("Phone", request.PhoneColumn),
            ("Location", request.LocationColumn),
            ("Status", request.StatusColumn),
            ("Notes", request.NotesColumn)
        };

        var mapping = new Dictionary<string, int>();

        foreach ((string field, int? selectedColumn) in selectedColumns)
        {
            if (selectedColumn == null)
            {
                continue;
            }

            int column = selectedColumn.Value;

            if (column < 0 || column >= columnCount)
            {
                throw new ImportException(ImportErrorType.Invalid,
                    $"{field} uses column {column}, but the file has columns 0 to {columnCount - 1}.");
            }

            if (mapping.ContainsValue(column))
            {
                throw new ImportException(ImportErrorType.Invalid,
                    $"{field} uses column {column}, which is already mapped to another field.");
            }

            mapping[field] = column;
        }

        return mapping;
    }

    private static string? GetDraftValue(List<string> cells, Dictionary<string, int> mapping, string field)
    {
        // A short row may not reach the mapped column.
        if (!mapping.TryGetValue(field, out int column) || column >= cells.Count)
        {
            return null;
        }

        string value = cells[column].Trim();
        return value.Length == 0 ? null : value;
    }

    private static int CalculateTotalPages(int totalCount, int pageSize)
    {
        return (int)Math.Ceiling(totalCount / (double)pageSize);
    }

    private static List<string> ReadStringList(string json)
    {
        return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
    }

    private static ImportBatchDto ToBatchDto(ImportBatch batch, int rowCount)
    {
        Dictionary<string, int>? mapping = null;
        if (batch.ColumnMappingJson != null)
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, int>>(batch.ColumnMappingJson);
        }

        return new ImportBatchDto
        {
            Id = batch.Id,
            FileName = batch.FileName,
            Status = batch.Status.ToString(),
            RowCount = rowCount,
            Headers = ReadStringList(batch.OriginalHeadersJson),
            Job = new ImportJobDto
            {
                Kind = batch.Job.Kind.ToString(),
                Stage = batch.Job.Stage.ToString(),
                IsActive = batch.Job.IsActive,
                ProcessedRows = batch.Job.ProcessedRows,
                TotalRows = batch.Job.TotalRows,
                Error = batch.Job.Error
            },
            Mapping = mapping,
            CommitSummary = batch.Status == ImportBatchStatus.Committed ? ToCommitSummary(batch) : null
        };
    }

    private static ImportRowDto ToRowDto(ImportRow row, Customer? matchedCustomer)
    {
        var dto = new ImportRowDto
        {
            Id = row.Id,
            SourceRowNumber = row.SourceRowNumber,
            OriginalCells = ReadStringList(row.OriginalCellsJson),
            DraftName = row.DraftName,
            DraftCompany = row.DraftCompany,
            DraftEmail = row.DraftEmail,
            DraftPhone = row.DraftPhone,
            DraftLocation = row.DraftLocation,
            DraftStatus = row.DraftStatus,
            DraftNotes = row.DraftNotes,
            IsCorrected = row.IsCorrected,
            MatchResult = row.MatchResult?.ToString(),
            MatchedCustomerId = row.MatchedCustomerId,
            ReviewDecision = row.ReviewDecision.ToString(),
            DecisionReason = row.DecisionReason
        };

        foreach (ImportIssue issue in row.Issues)
        {
            dto.Issues.Add(new ImportIssueDto { FieldName = issue.FieldName, Message = issue.Message });
        }

        if (row.MatchedCustomerId != null && matchedCustomer != null)
        {
            dto.MatchedCustomerVersion = matchedCustomer.RowVersion;

            if (row.MatchResult == MatchResult.Conflict)
            {
                dto.Changes = FindChanges(matchedCustomer, row);
            }
        }

        return dto;
    }

    private record ParsedImportFile(List<string> Headers, List<ImportRow> Rows);
}
