using DataBridge.Models;
using DataBridge.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DataBridge.Controllers;

public class AccountController : ControllerBase
{
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly IAntiforgery _antiforgery;
    private readonly GuestWorkspaceService _guestWorkspaces;

    public AccountController(
        SignInManager<IdentityUser> signInManager,
        UserManager<IdentityUser> userManager,
        IAntiforgery antiforgery,
        GuestWorkspaceService guestWorkspaces)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _antiforgery = antiforgery;
        _guestWorkspaces = guestWorkspaces;
    }

    [HttpPost("account/guest-token")]
    [AllowAnonymous]
    [EnableRateLimiting(GuestWorkspaceService.EntryRateLimit)]
    public async Task<IActionResult> IssueGuestToken()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Conflict();
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(new { token = await _guestWorkspaces.IssueTokenAsync() });
    }

    [HttpPost("account/guest")]
    [AllowAnonymous]
    [EnableRateLimiting(GuestWorkspaceService.EntryRateLimit)]
    public async Task<IActionResult> EnterGuestWorkspace([FromForm] string? token)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect("/workspace");
        }

        GuestWorkspace? workspace = await _guestWorkspaces.RedeemAsync(token);
        if (workspace == null)
        {
            return LocalRedirect("/?guest=expired#try");
        }

        await HttpContext.SignInAsync(IdentityConstants.ApplicationScheme,
            GuestWorkspaceService.CreatePrincipal(workspace.Id), new AuthenticationProperties
            {
                ExpiresUtc = new DateTimeOffset(workspace.SessionExpiresAtUtc!.Value, TimeSpan.Zero),
                AllowRefresh = false,
                IsPersistent = false
            });
        return LocalRedirect("/workspace");
    }

    // POST /account/login, from the form on Login.razor
    [HttpPost("account/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromForm] string? email, [FromForm] string? password, [FromForm] string? returnUrl)
    {
        // Only ever redirects inside this site, so a crafted link can't send a
        // user somewhere else after they sign in.
        string safeReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/workspace";

        Microsoft.AspNetCore.Identity.SignInResult result = Microsoft.AspNetCore.Identity.SignInResult.Failed;
        if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password))
        {
            result = await _signInManager.PasswordSignInAsync(email, password, isPersistent: false, lockoutOnFailure: true);
        }

        if (result.Succeeded)
        {
            return LocalRedirect(safeReturnUrl);
        }

        string error = result.IsLockedOut ? "locked" : "invalid";
        return LocalRedirect($"/login?error={error}&returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
    }

    // POST /account/logout
    [HttpPost("account/logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        if (User.IsInRole(AppRoles.Guest))
        {
            await _guestWorkspaces.EndAsync(User);
            await _signInManager.SignOutAsync();
            return LocalRedirect("/");
        }

        // A new security stamp ends this user's other copies of the cookie too,
        // including the one that open Blazor pages send to the API.
        IdentityUser? user = await _userManager.GetUserAsync(User);
        if (user != null)
        {
            await _userManager.UpdateSecurityStampAsync(user);
        }

        await _signInManager.SignOutAsync();

        return LocalRedirect("/login");
    }

    // GET /api/account/antiforgery, for API clients such as DataBridge.http. Send the
    // token back in the X-CSRF-TOKEN header. Get a new one after signing in.
    [HttpGet("api/account/antiforgery")]
    [AllowAnonymous]
    public IActionResult GetAntiforgeryToken()
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);

        return Ok(new { token = tokens.RequestToken });
    }
}
