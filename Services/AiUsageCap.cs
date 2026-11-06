using DataBridge.Data;
using DataBridge.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Services;

// A limit on paid AI calls per UTC day across all workspaces. The count is in the
// database, so it holds for concurrent requests and survives a restart.
public class AiUsageCap : IAiUsageCap
{
    private const int DefaultMaxCallsPerDay = 1000;

    private readonly DataBridgeDbContext _db;
    private readonly int _maxCallsPerDay;

    public AiUsageCap(DataBridgeDbContext db, IConfiguration configuration)
    {
        _db = db;
        _maxCallsPerDay = configuration.GetValue("OpenAI:MaxCallsPerDay", DefaultMaxCallsPerDay);
    }

    public async Task<bool> TryReserveCallAsync()
    {
        if (_maxCallsPerDay <= 0)
        {
            return false;
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (await TryAddToTodayAsync(today))
        {
            return true;
        }

        // Either today's row doesn't exist yet, or today is at the cap. Create the row if it's
        // missing, then try again. The UPDATE alone decides, because the row can appear at any
        // moment, and its existing doesn't mean that the cap has been reached.
        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"IF NOT EXISTS (SELECT 1 FROM AiUsageDays WHERE Day = {today}) INSERT INTO AiUsageDays (Day, Calls) VALUES ({today}, 0)");
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            // Another request created today's row first, which is all this needed.
        }

        return await TryAddToTodayAsync(today);
    }

    // One UPDATE statement, so two requests can't both take the last call.
    private async Task<bool> TryAddToTodayAsync(DateOnly today)
    {
        int updated = await _db.AiUsageDays
            .Where(usage => usage.Day == today && usage.Calls < _maxCallsPerDay)
            .ExecuteUpdateAsync(update => update.SetProperty(usage => usage.Calls, usage => usage.Calls + 1));

        return updated == 1;
    }
}
