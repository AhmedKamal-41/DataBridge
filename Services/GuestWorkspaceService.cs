using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DataBridge.Data;
using DataBridge.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Services;

public sealed class GuestWorkspaceService
{
    public const string EntryRateLimit = "guest-entry";
    private const string WorkspaceClaim = "databridge:guest-workspace";
    private const string GuestActorPrefix = "guest:";
    private const int TokenLength = 64;
    private readonly DataBridgeDbContext _db;

    public GuestWorkspaceService(DataBridgeDbContext db)
    {
        _db = db;
    }

    public async Task<string> IssueTokenAsync()
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _db.GuestWorkspaces.Add(new GuestWorkspace
        {
            Id = Guid.NewGuid(),
            TokenHash = Hash(token),
            TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(2)
        });
        await _db.SaveChangesAsync();
        return token;
    }

    public async Task<GuestWorkspace?> RedeemAsync(string? token)
    {
        if (token == null || token.Length != TokenLength)
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;
        DateTime expiresAt = now.AddMinutes(GuestWorkspace.SessionMinutes);
        string hash = Hash(token);
        IQueryable<GuestWorkspace> unusedToken = _db.GuestWorkspaces.Where(workspace =>
            workspace.TokenHash == hash && workspace.TokenExpiresAtUtc > now && workspace.SessionExpiresAtUtc == null);

        await using var transaction = await _db.Database.BeginTransactionAsync();
        // This update and the sample inserts commit together. Only one request can consume the token.
        int redeemed = await unusedToken.ExecuteUpdateAsync(update =>
            update.SetProperty(workspace => workspace.SessionExpiresAtUtc, expiresAt));
        if (redeemed == 0)
        {
            return null;
        }

        GuestWorkspace workspace = await _db.GuestWorkspaces.AsNoTracking()
            .SingleAsync(value => value.TokenHash == hash);
        _db.Customers.AddRange(
            SampleCustomer(workspace.Id, "Avery Patel", "Northline Supply", "avery@example.test", "Queens", CustomerStatus.Active),
            SampleCustomer(workspace.Id, "Morgan Lee", "Fieldwork Studio", "morgan@example.test", "Brooklyn", CustomerStatus.Active),
            SampleCustomer(workspace.Id, "Jordan Reed", "Evergreen Goods", "jordan@example.test", "Albany", CustomerStatus.Inactive));
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return workspace;
    }

    public static ClaimsPrincipal CreatePrincipal(Guid workspaceId)
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, $"{GuestActorPrefix}{workspaceId:N}"),
            new(ClaimTypes.Name, "Guest"),
            new(ClaimTypes.Role, AppRoles.Guest),
            new(WorkspaceClaim, workspaceId.ToString())
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme));
    }

    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.IsInRole(AppRoles.Guest) != true)
        {
            await SecurityStampValidator.ValidatePrincipalAsync(context);
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<DataBridgeDbContext>();
        DateTime now = DateTime.UtcNow;
        if (TryGetWorkspaceId(context.Principal, out Guid workspaceId) &&
            await db.GuestWorkspaces.AnyAsync(workspace => workspace.Id == workspaceId && workspace.SessionExpiresAtUtc > now))
        {
            db.WorkspaceId = workspaceId;
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }

    public async Task EndAsync(ClaimsPrincipal user)
    {
        if (TryGetWorkspaceId(user, out Guid workspaceId))
        {
            DateTime now = DateTime.UtcNow;
            await _db.GuestWorkspaces.Where(workspace => workspace.Id == workspaceId && workspace.SessionExpiresAtUtc > now)
                .ExecuteUpdateAsync(update => update.SetProperty(workspace => workspace.SessionExpiresAtUtc, now));
        }
    }

    public async Task<bool> ReserveAiSearchAsync()
    {
        DateTime now = DateTime.UtcNow;
        int updated = await _db.GuestWorkspaces.Where(workspace =>
                workspace.Id == _db.WorkspaceId && workspace.SessionExpiresAtUtc > now && workspace.AiSearches < GuestWorkspace.MaxAiSearches)
            .ExecuteUpdateAsync(update => update.SetProperty(workspace => workspace.AiSearches, workspace => workspace.AiSearches + 1));
        return updated == 1;
    }

    public async Task ReleaseAiSearchAsync()
    {
        await _db.GuestWorkspaces.Where(workspace => workspace.Id == _db.WorkspaceId && workspace.AiSearches > 0)
            .ExecuteUpdateAsync(update => update.SetProperty(workspace => workspace.AiSearches, workspace => workspace.AiSearches - 1));
    }

    public async Task DeleteExpiredAsync()
    {
        DateTime now = DateTime.UtcNow;
        DateTime gracePeriod = now.AddHours(-1);
        DateTime recentlyUpdated = now.AddMinutes(-15);
        await _db.GuestWorkspaces.Where(workspace =>
                workspace.SessionExpiresAtUtc == null && workspace.TokenExpiresAtUtc <= now)
            .ExecuteDeleteAsync();

        List<Guid> expiredWorkspaces = await _db.GuestWorkspaces
            .Where(workspace => workspace.Id != Guid.Empty && workspace.SessionExpiresAtUtc <= gracePeriod)
            .Select(workspace => workspace.Id)
            .ToListAsync();
        foreach (Guid workspaceId in expiredWorkspaces)
        {
            _db.WorkspaceId = workspaceId;
            if (await _db.ImportBatchJobs.AnyAsync(job => job.UpdatedAtUtc > recentlyUpdated &&
                job.Stage != ImportJobStage.Completed && job.Stage != ImportJobStage.Failed))
            {
                continue;
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();
            await _db.ImportAuditEntries.ExecuteDeleteAsync();
            await _db.ImportBatches.ExecuteDeleteAsync();
            await _db.Customers.ExecuteDeleteAsync();
            await _db.GuestWorkspaces.Where(workspace => workspace.Id == workspaceId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
    }

    public static bool IsGuestActor(string actor) => actor.StartsWith(GuestActorPrefix, StringComparison.Ordinal);

    private static bool TryGetWorkspaceId(ClaimsPrincipal user, out Guid workspaceId) =>
        Guid.TryParse(user.FindFirstValue(WorkspaceClaim), out workspaceId) && workspaceId != Guid.Empty;

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static Customer SampleCustomer(Guid workspaceId, string name, string company, string email,
        string location, CustomerStatus status) => new()
        {
            WorkspaceId = workspaceId,
            Name = name,
            Company = company,
            Email = email,
            NormalizedEmail = email,
            Location = location,
            Status = status
        };
}
