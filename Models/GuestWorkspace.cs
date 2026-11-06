namespace DataBridge.Models;

public sealed class GuestWorkspace
{
    public const int SessionMinutes = 60;
    public const int MaxImports = 5;
    public const int MaxAiSearches = 5;
    public const int MaxRowsPerImport = 200;
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxExpandedWorkbookBytes = 8 * 1024 * 1024;

    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime TokenExpiresAtUtc { get; set; }
    public DateTime? SessionExpiresAtUtc { get; set; }
    public int Imports { get; set; }
    public int AiSearches { get; set; }
}
