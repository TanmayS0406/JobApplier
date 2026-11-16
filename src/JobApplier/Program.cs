using System.Text.Json;
using System.Text.Json.Nodes;
using JobApplier.Agents;
using JobApplier.Claude;
using JobApplier.Infrastructure;
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
builder.Services.AddSingleton<GeminiAccountService>();
builder.Services.AddHttpClient<GeminiKeyChecker>(client => client.Timeout = TimeSpan.FromSeconds(10));

// Gmail replies: Claude reads the mailbox through the Gmail connector of the user's own Claude account (read-only tools only),
// once the user has turned tracking on.
builder.Services.AddSingleton<IMailReader, ClaudeMailReader>();
builder.Services.AddSingleton<ReplyStore>();
builder.Services.AddSingleton<MailChecker>();
builder.Services.AddHostedService<MailWatcher>();

builder.Services.AddSingleton<JobStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobStore>());

builder.Services.AddSingleton<JobMcpServer>();
builder.Services.AddSingleton<McpEndpointInfo>();
builder.Services.AddSingleton<AgentFactory>();
builder.Services.AddSingleton<ProfileImporter>();
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
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());

builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

// Screenshots: up to 30 images of 20 MB per upload.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 700L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 700L * 1024 * 1024);

var app = builder.Build();

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
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; form-action 'self'";
    await next();
});

app.UseStaticFiles();
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
