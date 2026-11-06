using DataBridge.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private const int ProbeTimeoutSeconds = 2;

    private readonly DataBridgeDbContext _db;
    private readonly ILogger<HealthController> _logger;

    public HealthController(DataBridgeDbContext db, ILogger<HealthController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // GET /api/health/live: the app process can answer. It doesn't touch the database.
    [HttpGet("live")]
    public IActionResult Live()
    {
        return Ok(new { status = "Alive" });
    }

    // GET /api/health/ready: the app can do real work, which needs SQL Server.
    [HttpGet("ready")]
    public async Task<IActionResult> Ready()
    {
        // Pooling is off so a pooled connection can't hide an outage.
        var connectionString = new SqlConnectionStringBuilder(_db.Database.GetConnectionString())
        {
            ConnectTimeout = ProbeTimeoutSeconds,
            ConnectRetryCount = 0,
            Pooling = false
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeTimeoutSeconds + 1));

        try
        {
            await using var connection = new SqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync(timeout.Token);

            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = ProbeTimeoutSeconds;
            await command.ExecuteScalarAsync(timeout.Token);

            return Ok(new { status = "Ready" });
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogWarning("The readiness probe couldn't reach SQL Server: {ErrorType}.", exception.GetType().Name);

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "Unavailable" });
        }
    }
}
