using System.Net.Http.Json;
using System.Text.Json;
using DataBridge.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace DataBridge.Services;

// How the Blazor pages reach the controllers. It only sends HTTP requests: every
// business rule stays in the services behind the controllers.
public class DataBridgeApiClient
{
    public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";

    private readonly HttpClient _httpClient;
    private readonly BrowserSession _session;

    public DataBridgeApiClient(HttpClient httpClient, BrowserSession session)
    {
        _httpClient = httpClient;
        _session = session;
    }

    public Task<PagedResponse<ImportBatchDto>> GetBatchesAsync(int page, int pageSize)
    {
        return SendAsync<PagedResponse<ImportBatchDto>>(HttpMethod.Get, $"api/imports?page={page}&pageSize={pageSize}");
    }

    public Task<ImportBatchDto> GetBatchAsync(int batchId)
    {
        return SendAsync<ImportBatchDto>(HttpMethod.Get, $"api/imports/{batchId}");
    }

    public Task<ImportAcceptedDto> UploadAsync(string fileName, Stream fileStream)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(fileStream), "file", fileName);

        return SendAsync<ImportAcceptedDto>(HttpMethod.Post, "api/imports", content);
    }

    public Task<ImportBatchDto> SaveMappingAsync(int batchId, SaveColumnMappingRequest request)
    {
        return SendAsync<ImportBatchDto>(HttpMethod.Put, $"api/imports/{batchId}/mapping", JsonContent.Create(request));
    }

    public Task<ImportAcceptedDto> ValidateAsync(int batchId)
    {
        return SendAsync<ImportAcceptedDto>(HttpMethod.Post, $"api/imports/{batchId}/validate");
    }

    public Task<PagedResponse<ImportRowDto>> GetRowsAsync(int batchId, int page, int pageSize)
    {
        return SendAsync<PagedResponse<ImportRowDto>>(HttpMethod.Get, $"api/imports/{batchId}/rows?page={page}&pageSize={pageSize}");
    }

    public Task<ImportRowDto> CorrectRowAsync(int batchId, int rowId, CorrectImportRowRequest request)
    {
        return SendAsync<ImportRowDto>(HttpMethod.Put, $"api/imports/{batchId}/rows/{rowId}", JsonContent.Create(request));
    }

    public Task<ImportRowDto> SaveDecisionAsync(int batchId, int rowId, SaveRowDecisionRequest request)
    {
        return SendAsync<ImportRowDto>(HttpMethod.Put, $"api/imports/{batchId}/rows/{rowId}/decision", JsonContent.Create(request));
    }

    public Task<ImportCommitSummaryDto> CommitAsync(int batchId)
    {
        return SendAsync<ImportCommitSummaryDto>(HttpMethod.Post, $"api/imports/{batchId}/commit");
    }

    public Task<PagedResponse<ImportAuditEntryDto>> GetAuditAsync(int batchId, int page, int pageSize)
    {
        return SendAsync<PagedResponse<ImportAuditEntryDto>>(HttpMethod.Get, $"api/imports/{batchId}/audit?page={page}&pageSize={pageSize}");
    }

    public Task<PagedResponse<CustomerDto>> GetCustomersAsync(int page, int pageSize, string? location, string? status)
    {
        string url = $"api/customers?page={page}&pageSize={pageSize}";

        if (!string.IsNullOrWhiteSpace(location))
        {
            url += $"&location={Uri.EscapeDataString(location)}";
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            url += $"&status={Uri.EscapeDataString(status)}";
        }

        return SendAsync<PagedResponse<CustomerDto>>(HttpMethod.Get, url);
    }

    public Task<CustomerAiSearchResultDto> SearchCustomersWithAiAsync(string question)
    {
        var request = new CustomerAiSearchRequest { Question = question };

        return SendAsync<CustomerAiSearchResultDto>(HttpMethod.Post, "api/customers/search/ai", JsonContent.Create(request));
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };

        if (_session.CookieHeader != null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _session.CookieHeader);
        }

        if (method != HttpMethod.Get && _session.AntiforgeryToken != null)
        {
            request.Headers.TryAddWithoutValidation(AntiforgeryHeaderName, _session.AntiforgeryToken);
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException((int)response.StatusCode, await ReadErrorMessageAsync(response));
            }

            T? body = await response.Content.ReadFromJsonAsync<T>();
            if (body == null)
            {
                throw new ApiException((int)response.StatusCode, "The API returned an empty response.");
            }

            return body;
        }
        catch (HttpRequestException)
        {
            throw new ApiException(0, "The API can't be reached. Check that the app is running and try again.");
        }
        catch (TaskCanceledException)
        {
            throw new ApiException(0, "The API took too long to respond. Try again.");
        }
    }

    // The controllers return ProblemDetails: Detail for a service error, and
    // Errors when [ApiController] rejects the request.
    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return "Your session has ended. Return to the DataBridge home page to start again.";
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return "Your account doesn't have permission to do that.";
        }

        string fallback = $"The request failed with status {(int)response.StatusCode}.";

        try
        {
            ValidationProblemDetails? problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
            if (problem == null)
            {
                return fallback;
            }

            if (!string.IsNullOrWhiteSpace(problem.Detail))
            {
                return problem.Detail;
            }

            if (problem.Errors.Count > 0)
            {
                return string.Join(" ", problem.Errors.SelectMany(error => error.Value));
            }

            return problem.Title ?? fallback;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return fallback;
        }
    }
}
