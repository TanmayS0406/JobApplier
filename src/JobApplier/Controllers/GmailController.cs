using JobApplier.Mail;
using JobApplier.Models;
using JobApplier.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Controllers;

/// <summary>
/// Reply tracking: Claude reads the user's Gmail through the Gmail connector of their Claude account. There is no Google sign-in in this
/// app any more; connecting is done in Claude (claude.ai, or its /mcp screen). Every POST here is covered by the antiforgery filter.
/// </summary>
public class GmailController : Controller
{
    private readonly IMailReader _reader;
    private readonly MailChecker _checker;
    private readonly ReplyStore _replies;
    private readonly AccountService _accounts;
    private readonly JobStore _jobs;
    private readonly SettingsService _settings;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GmailController> _logger;

    public GmailController(IMailReader reader, MailChecker checker, ReplyStore replies, AccountService accounts, JobStore jobs, SettingsService settings,
                           IHostApplicationLifetime lifetime, ILogger<GmailController> logger)
    {
        _reader = reader;
        _checker = checker;
        _replies = replies;
        _accounts = accounts;
        _jobs = jobs;
        _settings = settings;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Everything the Settings card shows. <paramref name="force"/> asks a live Claude session about the connector again (a few seconds);
    /// otherwise a recent answer is reused, and while a check is running nothing new is started.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Status(bool force, CancellationToken cancellationToken)
    {
        GmailConnectorStatus status;
        if (_checker.IsRunning && _reader.Cached is { } cached) status = cached;
        else if (_checker.IsRunning) status = GmailConnectorStatus.Of(ConnectorState.Unknown, "Looking through the mailbox now...");
        else
        {
            try
            {
                status = await _reader.GetStatusAsync(force, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The page was reloaded or closed while Claude was being asked (a few seconds). Nobody is waiting for the answer, and
                // letting the cancellation out of this action would show up as an unhandled exception (a Visual Studio break, a log entry).
                return new EmptyResult();
            }
        }

        var state = _replies.State();
        Response.Headers.CacheControl = "no-store";
        return Json(new
        {
            state = status.State.ToString().ToLowerInvariant(),
            ready = status.Ready,
            detail = status.Detail,
            readTools = status.ReadTools.Select(t => t[(t.LastIndexOf("__", StringComparison.Ordinal) + 2)..]).ToList(),
            enabled = state.Enabled,
            checking = _checker.IsRunning,
            lastCheckedUtc = state.LastCheckedUtc,
            lastError = state.LastError,
            replyCount = _replies.Count,
            autoHours = _settings.Current.MailAutoCheckHours,
            model = _settings.Current.MailModel
        });
    }

    /// <summary>Opens Claude's own connector screen (<c>/mcp</c>) in a console window, where the Gmail connector is signed in to Google.</summary>
    [HttpPost]
    public IActionResult Connect()
    {
        var error = _reader.OpenConnectWindow();
        return Json(new { ok = error == null, error });
    }

    /// <summary>
    /// Turns reply tracking on and starts the first check in the background - but only when Claude can actually use the Gmail connector
    /// right now; otherwise nothing is turned on and the answer says what is missing.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Enable(CancellationToken cancellationToken)
    {
        GmailConnectorStatus status;
        try
        {
            status = await _reader.GetStatusAsync(false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();   // the browser gave up; nothing was turned on
        }
        if (!status.Ready) return Json(new { ok = false, message = status.Detail });

        _replies.SetEnabled(true);
        _ = Task.Run(() => CheckInBackgroundAsync(_lifetime.ApplicationStopping));
        return Json(new { ok = true, message = "Reply tracking is on. The first check is running; it can take a minute or two." });
    }

    /// <summary>Turns reply tracking off. The replies already found are kept.</summary>
    [HttpPost]
    public IActionResult Disable()
    {
        _replies.SetEnabled(false);
        return Json(new { ok = true, message = "Reply tracking is off. Nothing reads your mailbox any more; the replies already found are kept." });
    }

    /// <summary>Looks through the mailbox now. <paramref name="rescan"/> looks back to the first application again.</summary>
    [HttpPost]
    public async Task<IActionResult> Check([FromForm] bool rescan, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _checker.CheckAsync(rescan, cancellationToken);
            return Json(new { ok = result.Ok, message = result.Message, newReplies = result.NewReplies });
        }
        catch (OperationCanceledException)
        {
            return Json(new { ok = false, message = "The check was cancelled." });
        }
    }

    /// <summary>Deletes the saved replies and the record of past checks (the mailbox itself is never touched).</summary>
    [HttpPost]
    public IActionResult ClearReplies()
    {
        _replies.Clear();
        return Json(new { ok = true, message = "The saved replies were deleted." });
    }

    /// <summary>"This mail is not about that application": the reply is removed and never matched again.</summary>
    [HttpPost]
    public IActionResult Dismiss(Guid id) => Json(new { ok = _replies.Dismiss(id) });

    /// <summary>The replies card of the dashboard. Polled by the page; <c>page</c>, <c>size</c>, <c>sort</c> and <c>min</c> (match at least, in percent) page, sort and filter it.</summary>
    [HttpGet]
    public IActionResult Replies(int? page, int? size, string? sort, int? min)
    {
        Response.Headers.CacheControl = "no-store";
        var query = ListQuery.Create(page, size, sort, min, ListQuery.ReplySorts);
        return PartialView("_Replies", RepliesViewModelBuilder.Build(_replies, _checker, _accounts, _jobs, query));
    }

    private async Task CheckInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _checker.CheckAsync(rescan: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // the app is shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The first mail check failed.");
        }
    }
}
