using DataBridge.DTOs;
using DataBridge.Interfaces;
using DataBridge.Models;
using DataBridge.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataBridge.Controllers;

[ApiController]
[Route("api/customers")]
[Authorize(Roles = AppRoles.AnyRole)]
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _customerService;
    private readonly ICustomerAiSearchService _aiSearchService;
    private readonly GuestWorkspaceService _guestWorkspaces;

    public CustomersController(ICustomerService customerService, ICustomerAiSearchService aiSearchService,
        GuestWorkspaceService guestWorkspaces)
    {
        _customerService = customerService;
        _aiSearchService = aiSearchService;
        _guestWorkspaces = guestWorkspaces;
    }

    // GET /api/customers?page=1&pageSize=25&location=Queens&status=Active
    [HttpGet]
    public async Task<IActionResult> GetCustomers(
        [FromQuery] PageRequest pageRequest,
        [FromQuery] string? location,
        [FromQuery] CustomerStatus? status)
    {
        var filters = new CustomerSearchFilters { Location = location, Status = status };

        return Ok(await _customerService.GetCustomersAsync(pageRequest.Page, pageRequest.PageSize, filters));
    }

    // POST /api/customers/search/ai
    [HttpPost("search/ai")]
    public async Task<IActionResult> SearchWithAi(CustomerAiSearchRequest request)
    {
        if (User.IsInRole(AppRoles.Guest) && !await _guestWorkspaces.ReserveAiSearchAsync())
        {
            return Problem(detail: "This guest workspace has used its AI searches. You can still use the location and status filters.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            return Ok(await _aiSearchService.SearchAsync(request.Question!));
        }
        catch (AiSearchException exception)
        {
            // The shared cap stopped this before any paid call, so it doesn't use up
            // one of the guest's own searches.
            if (exception.ErrorType == AiSearchErrorType.CapReached && User.IsInRole(AppRoles.Guest))
            {
                await _guestWorkspaces.ReleaseAiSearchAsync();
            }

            int statusCode = exception.ErrorType switch
            {
                AiSearchErrorType.InvalidOutput => StatusCodes.Status502BadGateway,
                AiSearchErrorType.CapReached => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status503ServiceUnavailable
            };

            return Problem(detail: exception.Message, statusCode: statusCode);
        }
    }
}
