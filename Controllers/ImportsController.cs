using DataBridge.DTOs;
using DataBridge.Interfaces;
using DataBridge.Models;
using DataBridge.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace DataBridge.Controllers;

[ApiController]
[Route("api/imports")]
[Authorize(Roles = AppRoles.AdminOrReviewer)]
public class ImportsController : ControllerBase
{
    private readonly IImportService _importService;

    // From the sign-in cookie, so a request can't claim to be someone else.
    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public ImportsController(IImportService importService)
    {
        _importService = importService;
    }

    // POST /api/imports
    [HttpPost]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Upload([FromForm] UploadImportRequest request)
    {
        try
        {
            using Stream fileStream = request.File!.OpenReadStream();
            ImportBatchDto batch = await _importService.UploadAsync(request.File.FileName, fileStream);

            return AcceptedBatch(batch);
        }
        catch (ImportException exception)
        {
            return ImportErrorResult(exception);
        }
    }

    // GET /api/imports?page=1&pageSize=25
    [HttpGet]
    public async Task<IActionResult> GetBatches([FromQuery] PageRequest pageRequest)
    {
        return Ok(await _importService.GetBatchesAsync(pageRequest.Page, pageRequest.PageSize));
    }

    // Not named GetBatchAsync: ASP.NET Core removes the Async suffix from action
    // names, so CreatedAtAction(nameof(GetBatchAsync)) above would fail.
    // GET /api/imports/5
    [HttpGet("{batchId}")]
    public async Task<IActionResult> GetBatch(int batchId)
    {
        ImportBatchDto? batch = await _importService.GetBatchAsync(batchId);
        if (batch == null)
        {
            return NotFound();
        }

        return Ok(batch);
    }

    // GET /api/imports/5/rows?page=1&pageSize=25
    [HttpGet("{batchId}/rows")]
    public async Task<IActionResult> GetRows(int batchId, [FromQuery] PageRequest pageRequest)
    {
        PagedResponse<ImportRowDto>? rows =
            await _importService.GetRowsAsync(batchId, pageRequest.Page, pageRequest.PageSize);

        if (rows == null)
        {
            return NotFound();
        }

        return Ok(rows);
    }

    // PUT /api/imports/5/mapping
    [HttpPut("{batchId}/mapping")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> SaveMapping(int batchId, SaveColumnMappingRequest request)
    {
        try
        {
            ImportBatchDto? batch = await _importService.SaveMappingAsync(batchId, request);
            if (batch == null)
            {
                return NotFound();
            }

            return Ok(batch);
        }
        catch (ImportException exception)
        {
            return ImportErrorResult(exception);
        }
    }

    // POST /api/imports/5/validate
    [HttpPost("{batchId}/validate")]
    public async Task<IActionResult> Validate(int batchId)
    {
        try
        {
            ImportBatchDto? batch = await _importService.ValidateAsync(batchId);
            if (batch == null)
            {
                return NotFound();
            }

            return AcceptedBatch(batch);
        }
        catch (ImportException exception)
        {
            return ImportErrorResult(exception);
        }
    }

    // PUT /api/imports/5/rows/12
    [HttpPut("{batchId}/rows/{rowId}")]
    public async Task<IActionResult> CorrectRow(int batchId, int rowId, CorrectImportRowRequest request)
    {
        try
        {
            ImportRowDto? row = await _importService.CorrectRowAsync(batchId, rowId, request, CurrentUserId);
            if (row == null)
            {
                return NotFound();
            }

            return Ok(row);
        }
        catch (ImportException exception)
        {
            return ImportErrorResult(exception);
        }
    }

    // PUT /api/imports/5/rows/12/decision
    [HttpPut("{batchId}/rows/{rowId}/decision")]
    public async Task<IActionResult> SaveDecision(int batchId, int rowId, SaveRowDecisionRequest request)
    {
        try
        {
            ImportRowDto? row = await _importService.SaveDecisionAsync(batchId, rowId, request, CurrentUserId);
            if (row == null)
            {
                return NotFound();
            }

            return Ok(row);
        }
        catch (ImportException exception)
        {
            return ImportErrorResult(exception);
        }
    }

    // POST /api/imports/5/commit
    [HttpPost("{batchId}/commit")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Commit(int batchId)
    {
        try
        {
            ImportCommitSummaryDto? summary = await _importService.CommitAsync(batchId, CurrentUserId);
            if (summary == null)
            {
                return NotFound();
            }

            return Ok(summary);
        }
        catch (ImportException exception)
        {
            return ImportErrorResult(exception);
        }
    }

    // GET /api/imports/5/audit?page=1&pageSize=25
    [HttpGet("{batchId}/audit")]
    public async Task<IActionResult> GetAudit(int batchId, [FromQuery] PageRequest pageRequest)
    {
        PagedResponse<ImportAuditEntryDto>? audit =
            await _importService.GetAuditAsync(batchId, pageRequest.Page, pageRequest.PageSize);

        if (audit == null)
        {
            return NotFound();
        }

        return Ok(audit);
    }

    // 202 Accepted: the work is saved and queued. GetBatch shows its progress.
    private AcceptedResult AcceptedBatch(ImportBatchDto batch)
    {
        string statusUrl = Url.Action(nameof(GetBatch), null, new { batchId = batch.Id }, Request.Scheme)!;

        return Accepted(statusUrl, new ImportAcceptedDto
        {
            BatchId = batch.Id,
            StatusUrl = statusUrl,
            Job = batch.Job
        });
    }

    private ObjectResult ImportErrorResult(ImportException exception)
    {
        int statusCode = exception.ErrorType switch
        {
            ImportErrorType.TooLarge => StatusCodes.Status413PayloadTooLarge,
            ImportErrorType.Unsupported => StatusCodes.Status415UnsupportedMediaType,
            ImportErrorType.Locked => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };

        return Problem(detail: exception.Message, statusCode: statusCode);
    }
}
