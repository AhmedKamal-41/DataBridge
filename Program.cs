using DataBridge.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using DataBridge.Data;
using DataBridge.Interfaces;
using DataBridge.Services;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// In Docker Compose, each file in this folder is one setting. The file name is the
// setting's name and its contents are the value, so secrets stay out of the image.
string? secretsDirectory = builder.Configuration["Secrets:Directory"];
if (!string.IsNullOrEmpty(secretsDirectory))
{
    builder.Configuration.AddKeyPerFile(secretsDirectory, optional: true);
}

// Keeps sign-in cookies and antiforgery tokens valid when a container is replaced.
string? dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrEmpty(dataProtectionKeysPath))
{
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Lets the app find and run classes in the Controllers folder. The filter checks the
// antiforgery token on every POST, PUT, and DELETE, including uploads and sign-in.
// The filter is part of the MVC view services, which is why this isn't AddControllers.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = DataBridgeApiClient.AntiforgeryHeaderName;
});

// Behind a reverse proxy, the client's address and scheme come from X-Forwarded-* headers.
// They're only believed from the proxies listed here, so a client can't fake them.
string[] trustedProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (string proxy in trustedProxies)
        {
            options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
        }
    });
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // A body keeps the status code pages middleware from re-running this POST as a page.
    options.OnRejected = (context, cancellationToken) => new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
        new ProblemDetails { Status = StatusCodes.Status429TooManyRequests, Title = "Too many requests." },
        options: null,
        contentType: "application/problem+json",
        cancellationToken));
    options.AddPolicy(GuestWorkspaceService.EntryRateLimit, context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 16, Window = TimeSpan.FromMinutes(5) }));
});

// Gives each request its own DataBridgeDbContext, connected to SQL Server.
string connectionString = builder.Configuration.GetConnectionString("DataBridge")
    ?? throw new InvalidOperationException("Connection string 'DataBridge' is missing.");

builder.Services.AddDbContext<DataBridgeDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 12;
    options.User.RequireUniqueEmail = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
})
    .AddEntityFrameworkStores<DataBridgeDbContext>()
    .AddDefaultTokenProviders();

// Checks the user against the database on every request, so signing out, or a
// changed role, takes effect at once for every copy of the cookie.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.Zero;
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/access-denied";
    options.ReturnUrlParameter = "returnUrl";
    options.Cookie.HttpOnly = true;
    options.Events.OnValidatePrincipal = GuestWorkspaceService.ValidateCookieAsync;
    options.Cookie.HttpOnly = true;

    // A browser page gets redirected. An API caller gets a status code, with a body
    // so that the status code pages middleware leaves the response alone.
    options.Events.OnRedirectToLogin = context =>
    {
        return WriteApiStatusOrRedirectAsync(context.HttpContext, StatusCodes.Status401Unauthorized, context.RedirectUri);
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        return WriteApiStatusOrRedirectAsync(context.HttpContext, StatusCodes.Status403Forbidden, context.RedirectUri);
    };
});

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddScoped<BrowserSession>();
builder.Services.AddScoped<GuestWorkspaceService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<IImportService>(services => services.GetRequiredService<ImportService>());
builder.Services.AddScoped<IImportJobWork>(services => services.GetRequiredService<ImportService>());
builder.Services.AddScoped<IImportJobQueue, ImportJobQueue>();
builder.Services.AddScoped<ImportJobRunner>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<ICustomerAiSearchService, CustomerAiSearchService>();
builder.Services.AddScoped<IAiUsageCap, AiUsageCap>();
builder.Services.AddScoped<ImportFileRetentionService>();

// The API key is read from server configuration, such as user secrets. Without it the
// app still runs, and AI search reports that it isn't set up.
builder.Services.AddHttpClient<IOpenAiFilterClient, OpenAiFilterClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["OpenAI:BaseUrl"]
        ?? throw new InvalidOperationException("Setting 'OpenAI:BaseUrl' is missing."));
    client.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("OpenAI:TimeoutSeconds", 20));
});

// The Blazor pages run on the server and call this same app's controllers. This
// address must be one the server can reach itself on, not the browser's address,
// which may be a forwarded Codespaces URL.
string apiBaseUrl = builder.Configuration["Api:BaseUrl"]
    ?? throw new InvalidOperationException("Setting 'Api:BaseUrl' is missing.");

builder.Services.AddHttpClient<DataBridgeApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
})
    // The client sets the Cookie header itself, one signed-in user at a time. A shared
    // cookie store here could send one user's session with another user's request.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });

// Hangfire keeps its queue in SQL Server, in its own HangFire schema, which it creates
// itself. It isn't part of the EF Core migrations.
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(() => new SqlConnection(connectionString), new SqlServerStorageOptions
    {
        // How long after a crash Hangfire gives a running job to another worker.
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(1),
        QueuePollInterval = TimeSpan.FromSeconds(1),

        // Off in production, where the runtime login can't change the schema and the
        // migration step installs it instead.
        PrepareSchemaIfNecessary = builder.Configuration.GetValue("Hangfire:PrepareSchema", true)
    }));

if (builder.Configuration.GetValue("Hangfire:WorkerEnabled", true))
{
    builder.Services.AddHangfireServer();
}

var app = builder.Build();

// The Compose "migrate" service runs this once, and the app starts after it succeeds.
if (args.Contains("--apply-migrations"))
{
    using IServiceScope migrationScope = app.Services.CreateScope();
    await migrationScope.ServiceProvider.GetRequiredService<DataBridgeDbContext>().Database.MigrateAsync();

    await using (var hangfireConnection = new SqlConnection(connectionString))
    {
        await hangfireConnection.OpenAsync();
        SqlServerObjectsInstaller.Install(hangfireConnection);
    }

    return;
}

await IdentitySeeder.SeedAsync(
    app.Services,
    app.Configuration,
    seedUsers: app.Environment.IsDevelopment() || app.Configuration.GetValue("Seed:Enabled", false));

// Runs every minute, and now at startup, to queue work that a crash left behind.
var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobs.AddOrUpdate<IImportJobQueue>("recover-import-jobs", queue => queue.RecoverPendingWorkAsync(), Cron.Minutely());
recurringJobs.Trigger("recover-import-jobs");
recurringJobs.AddOrUpdate<GuestWorkspaceService>("delete-expired-guest-workspaces", workspace => workspace.DeleteExpiredAsync(), Cron.Hourly());
recurringJobs.AddOrUpdate<ImportFileRetentionService>("delete-expired-import-files", retention => retention.DeleteExpiredFilesAsync(), Cron.Hourly());
if (app.Configuration.GetValue("Import:RawFileRetention:Enabled", false))
{
    recurringJobs.Trigger("delete-expired-import-files");
}

// Configure the HTTP request pipeline.
if (trustedProxies.Length > 0)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapControllers();

app.Run();

static Task WriteApiStatusOrRedirectAsync(HttpContext context, int statusCode, string redirectUri)
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = statusCode, Title = statusCode == 401 ? "Sign in required." : "Permission denied." },
            options: null,
            contentType: "application/problem+json");
    }

    context.Response.Redirect(redirectUri);
    return Task.CompletedTask;
}
