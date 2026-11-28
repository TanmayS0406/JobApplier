using System.Text.Json;
using System.Text.Json.Nodes;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.SqlServer;
using JobApplier.Agents;
using JobApplier.Claude;
using JobApplier.Persistence;
using JobApplier.Infrastructure;
using JobApplier.Insights;
using JobApplier.Interview;
using JobApplier.Mail;
using JobApplier.Mcp;
using JobApplier.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

// A published build keeps appsettings.json and wwwroot next to the executable. Use that folder even when the app is started
// from somewhere else (a shortcut, Task Scheduler); under "dotnet run" there is no wwwroot there, so the default applies.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null
});

// ---- services ----
var paths = AppPaths.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<HumanHelpBroker>();
builder.Services.AddSingleton<ClaudeAccountService>();
builder.Services.AddSingleton<CopilotAccountService>();

// Gmail replies: Claude reads the mailbox through the Gmail connector of the user's own Claude account (read-only tools only),
// once the user has turned tracking on. Answering is separate: Claude writes a draft (read-only again) and a mail is sent only when the
// user presses Send on a text they have read, by a session that is given the connector's reply tool and nothing else.
builder.Services.AddSingleton<IMailReader, ClaudeMailReader>();
builder.Services.AddSingleton<ReplyStore>();
builder.Services.AddSingleton<MailChecker>();
builder.Services.AddSingleton<ReplyComposer>();
builder.Services.AddSingleton<InterviewGenerator>();
builder.Services.AddSingleton<VerificationMailService>();
builder.Services.AddHostedService<MailWatcher>();

// The jobs live in SQL Server (EF Core; the schema is migrated every time the app starts, see below) and the queue that runs them is
// Hangfire, in the same database, so neither is lost when the app is closed.
var connectionString = DatabaseStartup.ConnectionString(builder.Configuration);
builder.Services.AddDbContextFactory<JobApplierDbContext>(options => JobApplierDbOptions.Configure(options, connectionString));
builder.Services.AddSingleton<IJobPersistence, EfJobPersistence>();

// Registered before the Hangfire server below: hosted services stop in reverse order, so the last writes of the jobs that are
// being wound down are flushed to the database after the server has stopped.
builder.Services.AddSingleton<JobStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobStore>());

builder.Services.AddSingleton<JobMcpServer>();
builder.Services.AddSingleton<McpEndpointInfo>();
builder.Services.AddSingleton<AgentFactory>();
builder.Services.AddSingleton<ProfileImporter>();

// Resume gap analysis (ML.NET text model, Math.NET statistics) and, on request, Claude's advice on it.
builder.Services.AddSingleton<GapAnalysisService>();
builder.Services.AddSingleton<ICoachRunner, AgentCoachRunner>();
builder.Services.AddSingleton<ResumeCoachService>();
// The runner is built from the pipeline services, so the pipeline reaches it through a delegate resolved on first use.
builder.Services.AddSingleton(sp => new PipelineServices(
    sp.GetRequiredService<AgentFactory>(),
    sp.GetRequiredService<JobStore>(),
    sp.GetRequiredService<ProfileService>(),
    sp.GetRequiredService<AccountService>(),
    sp.GetRequiredService<AppPaths>(),
    jobId => sp.GetRequiredService<JobRunner>().Enqueue(jobId),
    until => sp.GetRequiredService<JobRunner>().NoteUsageLimit(until)));
builder.Services.AddSingleton<JobRunner>();

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),   // a run killed with the app is picked up again after this at the latest (a restart does it at once)
        QueuePollInterval = TimeSpan.Zero,                      // long polling: a queued job starts at once
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true
    }));
builder.Services.AddSingleton<IJobQueue, HangfireJobQueue>();
builder.Services.AddHostedService<JobRecoveryService>();
builder.Services.AddHangfireServer(options =>
{
    options.Queues = JobQueues.All;
    // The most "jobs at the same time" Settings allows (8), plus room for the small recurring check. How many of them really run
    // at once is the user's setting, applied by the runner, so changing it needs no restart.
    options.WorkerCount = 10;
    options.ShutdownTimeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

// Screenshots: up to 30 images of 20 MB per upload.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 700L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 700L * 1024 * 1024);

var app = builder.Build();

// The database comes first: bring its schema up to date (creating it if needed), then move in the jobs of the earlier, file-based
// versions. Nothing reads a job before this, and Hangfire opens its own tables in the same database only once it is there.
DatabaseStartup.Migrate(app.Services);
LegacyJobImporter.Import(paths, app.Services.GetRequiredService<IJobPersistence>(), app.Logger);

// The Claude process needs to know where this app's MCP endpoint is once Kestrel has picked its address.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
    if (address != null && Uri.TryCreate(address.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1").Replace("*", "127.0.0.1"), UriKind.Absolute, out var uri))
    {
        var host = uri.Host is "localhost" or "[::1]" ? "127.0.0.1" : uri.Host;
        app.Services.GetRequiredService<McpEndpointInfo>().BaseUrl = $"{uri.Scheme}://{host}:{uri.Port}";
    }
    app.Logger.LogInformation("JobApplier is running at {Url}. Data folder: {Data}", address, paths.Root);
});
app.Lifetime.ApplicationStopping.Register(ClaudeCli.KillAll);

// ---- pipeline ----
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    // Hangfire's dashboard has its own inline script and styles, which this policy would block.
    if (!context.Request.Path.StartsWithSegments("/hangfire"))
    {
        headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; form-action 'self'";
    }
    await next();
});

app.UseStaticFiles();

// The queue's own dashboard: what is waiting, running, scheduled for after a usage limit, and the recurring check. This machine only.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "JobApplier queue",
    Authorization = new IDashboardAuthorizationFilter[] { new LocalRequestsOnlyAuthorizationFilter() }
});

app.UseRouting();

// The MCP endpoint the Claude processes call. Not part of the UI: guarded by a per-run bearer token.
app.MapPost("/mcp/{jobId:guid}", async (Guid jobId, HttpContext http, JobMcpServer mcp, JobStore store) =>
{
    if (!mcp.IsAuthorized(http.Request.Headers.Authorization.ToString())) return Results.Unauthorized();
    if (!store.Exists(jobId)) return Results.NotFound();

    JsonNode? message;
    try
    {
        message = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.Text(JobMcpServer.RpcError(null, -32700, "Parse error").ToJsonString(), "application/json", statusCode: 400);
    }
    if (message == null) return Results.BadRequest();

    var response = await mcp.HandleAsync(jobId, message, http.RequestAborted);
    return response == null
        ? Results.StatusCode(StatusCodes.Status202Accepted)
        : Results.Text(response.ToJsonString(), "application/json");
}).DisableAntiforgery();

// No server-initiated stream and no session to delete.
app.MapMethods("/mcp/{jobId:guid}", new[] { "GET", "DELETE" }, () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

// Lets the test project start the app if it ever needs to.
public partial class Program;
