using JobApplier.Claude;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Models;
using JobApplier.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Controllers;

public class SettingsController : Controller
{
    private readonly SettingsService _settings;
    private readonly AccountService _accounts;
    private readonly ClaudeAccountService _claude;
    private readonly CopilotAccountService _copilot;
    private readonly AppPaths _paths;

    public SettingsController(SettingsService settings, AccountService accounts, ClaudeAccountService claude, CopilotAccountService copilot, AppPaths paths)
    {
        _settings = settings;
        _accounts = accounts;
        _claude = claude;
        _copilot = copilot;
        _paths = paths;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Only the selected provider is asked: checking Claude means starting its CLI, which is pointless when Copilot is in use.
        if (AiProvider.Normalize(_settings.Current.Provider) == AiProvider.Copilot)
        {
            var copilot = _copilot.GetStatus();
            return View(Build(_settings.Current, copilot.CliFound, copilot.LoggedIn, copilot.Detail));
        }

        var status = await _claude.GetStatusAsync(cancellationToken);
        return View(Build(_settings.Current, status.CliFound, status.LoggedIn, status.Detail));
    }

    [HttpPost]
    public IActionResult Save([FromForm] AppSettings settings)
    {
        var error = _settings.Save(settings);
        if (error != null) TempData["Error"] = error;
        else TempData["Message"] = "Settings saved. Changes apply to the next job.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Polled by the page header and the Settings page. Cheap: the CLI result is cached for a few seconds.</summary>
    [HttpGet]
    public async Task<IActionResult> ClaudeStatus(bool force, CancellationToken cancellationToken)
    {
        var s = await _claude.GetStatusAsync(cancellationToken, force);
        Response.Headers.CacheControl = "no-store";
        return Json(new { cliFound = s.CliFound, loggedIn = s.LoggedIn, detail = s.Detail, plan = s.Plan });
    }

    /// <summary>GitHub Copilot sign-in state, in the same JSON shape as <see cref="ClaudeStatus"/> so the page script treats both alike.</summary>
    [HttpGet]
    public IActionResult CopilotStatus()
    {
        var s = _copilot.GetStatus();
        Response.Headers.CacheControl = "no-store";
        return Json(new { cliFound = s.CliFound, loggedIn = s.LoggedIn, detail = s.Detail, plan = (string?)null, source = s.Source, canInstall = s.CanInstall });
    }

    /// <summary>Opens a console window that installs the GitHub Copilot CLI.</summary>
    [HttpPost]
    public IActionResult CopilotInstall()
    {
        var error = _copilot.StartInstall();
        return Json(new { ok = error == null, error });
    }

    /// <summary>Starts the Copilot OAuth sign-in (<c>copilot login</c>): a console window plus the browser open on this PC.</summary>
    [HttpPost]
    public IActionResult CopilotSignIn()
    {
        var error = _copilot.StartSignIn();
        return Json(new { ok = error == null, error });
    }

    /// <summary>Opens the Copilot CLI with the instruction to type <c>/logout</c>: the CLI has no other way to sign out.</summary>
    [HttpPost]
    public IActionResult CopilotSignOut()
    {
        var error = _copilot.StartSignOut();
        return Json(new { ok = error == null, error });
    }

    /// <summary>Starts the Claude OAuth sign-in: a console window plus the browser open on this PC.</summary>
    [HttpPost]
    public IActionResult SignIn()
    {
        var error = _claude.StartSignIn();
        return Json(new { ok = error == null, error });
    }

    [HttpPost]
    public async Task<IActionResult> SignOut(CancellationToken cancellationToken)
    {
        var error = await _claude.SignOutAsync(cancellationToken);
        return Json(new { ok = error == null, error });
    }

    [HttpPost]
    public IActionResult RebuildDocument()
    {
        var error = _accounts.RewriteDocument();
        if (error != null) TempData["Error"] = error;
        else TempData["Message"] = "Accounts document rebuilt: " + _accounts.DocumentPath;
        return RedirectToAction(nameof(Index));
    }

    private SettingsViewModel Build(AppSettings settings, bool cliFound, bool signedIn, string detail)
    {
        var copilot = AiProvider.Normalize(settings.Provider) == AiProvider.Copilot;
        var checks = new List<SystemCheck>
        {
            copilot
                ? new SystemCheck("GitHub Copilot CLI", cliFound, _copilot.CliPath ?? "Not found. Install it with: " + CopilotCli.WingetInstallCommand + " (or " + CopilotCli.NpmInstallCommand + ")")
                : new SystemCheck("Claude Code CLI", cliFound, _claude.CliPath ?? "Not found. Install it from https://claude.com/claude-code"),
            copilot
                ? new SystemCheck("Signed in to GitHub Copilot", signedIn, signedIn ? "Yes: " + detail : "Not signed in - use the Sign in with GitHub button in the card above.")
                : new SystemCheck("Signed in to Claude", signedIn, signedIn ? "Yes" : "Not signed in - use the button above."),
            SystemChecks.Npx(),
            SystemChecks.Browser(settings.Browser),
            SystemChecks.Writable("Data folder", _paths.Root),
            SystemChecks.WritableFile("Accounts document", _settings.AccountsDocumentPath)
        };

        return new SettingsViewModel
        {
            Settings = settings,
            Provider = AiProvider.Normalize(settings.Provider),
            DataFolder = _paths.Root,
            AccountsDocumentPath = _settings.AccountsDocumentPath,
            DocumentError = _accounts.DocumentError,
            AccountCount = _accounts.All().Count,
            ApplicationCount = _accounts.Applications().Count,
            Checks = checks
        };
    }
}
