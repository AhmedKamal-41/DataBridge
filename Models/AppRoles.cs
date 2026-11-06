namespace DataBridge.Models;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Reviewer = "Reviewer";
    public const string Viewer = "Viewer";
    public const string Guest = "Guest";

    // [Authorize(Roles = ...)] takes a comma-separated list. Any one role is enough.
    public const string AdminOrReviewer = Admin + "," + Reviewer;
    public const string ImportReviewers = AdminOrReviewer + "," + Guest;
    public const string ImportManagers = Admin + "," + Guest;
    public const string AnyRole = Admin + "," + Reviewer + "," + Viewer + "," + Guest;

    public static readonly string[] All = { Admin, Reviewer, Viewer };
}
