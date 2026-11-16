using JobApplier.Mail;
using JobApplier.Models;
using JobApplier.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Controllers;

/// <summary>Connecting Gmail (read-only OAuth) and the replies found in it. Every POST here is covered by the antiforgery filter.</summary>
public class GmailController : Controller
{
    private readonly GmailConnection _gmail;
    private readonly MailChecker _checker;
    private readonly ReplyStore _replies;
    private readonly AccountService _accounts;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GmailController> _logger;

    public GmailController(GmailConnection gmail, MailChecker checker, ReplyStore replies, AccountService accounts,
                           IHostApplicationLifetime lifetime, ILogger<GmailController> logger)
    {
        _gmail = gmail;
        _checker = checker;
        _replies = replies;
        _accounts = accounts;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Everything the Settings card shows. Never contains the client secret or a token.</summary>
    [HttpGet]
    public IActionResult Status()
    {
        var status = _gmail.GetStatus();
        var state = _replies.State();
        Response.Headers.CacheControl = "no-store";
        return Json(new
        {
            hasClient = status.HasClient,
            clientHint = status.ClientHint,
            connected = status.Connected,
            needsReconnect = status.NeedsReconnect,
            email = status.Email,
            connectedUtc = status.ConnectedUtc,
            lastCheckedUtc = state.LastCheckedUtc,
            lastError = state.LastError,
            checking = _checker.IsRunning,
            replyCount = _replies.Count,
            redirectUri = RedirectUri()
        });
    }

    /// <summary>Saves the Client ID and secret of the Google client the user created. An empty secret keeps the saved one.</summary>
    [HttpPost]
    public IActionResult SaveClient([FromForm] string? clientId, [FromForm] string? clientSecret)
    {
        var error = _gmail.SaveClient(clientId, clientSecret);
        return Json(new { ok = error == null, message = error ?? "Client saved. Now press Connect Gmail." });
    }

    /// <summary>
    /// Returns the address of Google's consent page. The page then goes there with <c>location.assign</c>: the Content-Security-Policy
    /// <c>form-action 'self'</c> would also block a redirect to Google after a form post.
    /// </summary>
    [HttpPost]
    public IActionResult Connect()
    {
        if (!IsLoopback(Request.Host.Host))
            return Json(new { ok = false, error = "Open JobApplier as http://127.0.0.1:" + (Request.Host.Port ?? 5189) + " to connect Gmail: Google only returns to this PC's own address." });

        var url = _gmail.StartConnect(RedirectUri(), out var error);
        return Json(new { ok = url != null, url, error });
    }

    /// <summary>Where Google sends the browser back to after the consent page. The <c>state</c> proves this app started the sign-in.</summary>
    [HttpGet]
    public async Task<IActionResult> Callback(string? state, string? code, string? error, CancellationToken cancellationToken)
    {
        var (ok, message) = await _gmail.CompleteAsync(state, code, error, cancellationToken);
        TempData[ok ? "Message" : "Error"] = message;

        // Do not make the user wait for the first look at the mailbox: start it now and let the dashboard show what it finds.
        if (ok) _ = Task.Run(() => CheckInBackgroundAsync(_lifetime.ApplicationStopping));
        return RedirectToAction("Index", "Settings");
    }

    /// <summary>Looks through the mailbox now. <paramref name="rescan"/> reads old messages again, too.</summary>
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
            return Json(new { ok = false, message = "The check was cancelled; what was read so far is kept." });
        }
    }

    /// <summary>Tells Google to forget the connection and deletes the stored sign-in. The saved client is kept.</summary>
    [HttpPost]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        try
        {
            await _gmail.DisconnectAsync(cancellationToken);
            return Json(new { ok = true, message = "Gmail was disconnected. The replies already found are kept." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return Json(new { ok = false, message = "Could not disconnect: " + ex.Message });
        }
    }

    /// <summary>Disconnects and also forgets the Client ID and secret.</summary>
    [HttpPost]
    public async Task<IActionResult> RemoveClient(CancellationToken cancellationToken)
    {
        try
        {
            await _gmail.DisconnectAsync(cancellationToken);
            _gmail.RemoveEverything();
            return Json(new { ok = true, message = "Gmail was disconnected and the saved client was deleted." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return Json(new { ok = false, message = "Could not remove it: " + ex.Message });
        }
    }

    /// <summary>Deletes the saved replies and what was remembered about the mailbox (the mailbox itself is never touched).</summary>
    [HttpPost]
    public IActionResult ClearReplies()
    {
        _replies.Clear();
        return Json(new { ok = true, message = "The saved replies were deleted." });
    }

    /// <summary>"This mail is not about that application": the reply is removed and never matched again.</summary>
    [HttpPost]
    public IActionResult Dismiss(Guid id) => Json(new { ok = _replies.Dismiss(id) });

    /// <summary>The replies card of the dashboard. Polled by the page.</summary>
    [HttpGet]
    public IActionResult Replies()
    {
        Response.Headers.CacheControl = "no-store";
        return PartialView("_Replies", RepliesViewModelBuilder.Build(_gmail, _replies, _checker, _accounts));
    }

    private async Task CheckInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _checker.CheckAsync(rescan: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The first mail check after connecting failed.");
        }
    }

    private string RedirectUri() => $"{Request.Scheme}://{Request.Host}{Url.Action(nameof(Callback), "Gmail")}";

    private static bool IsLoopback(string host) => host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
}
