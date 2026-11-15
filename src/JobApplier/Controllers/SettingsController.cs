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
    private readonly GeminiAccountService _gemini;
    private readonly AppPaths _paths;

    public SettingsController(SettingsService settings, AccountService accounts, ClaudeAccountService claude, GeminiAccountService gemini, AppPaths paths)
    {
        _settings = settings;
        _accounts = accounts;
        _claude = claude;
        _gemini = gemini;
        _paths = paths;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Only the selected provider is asked: checking Claude means starting its CLI, which is pointless when Gemini is in use.
        if (AiProvider.Normalize(_settings.Current.Provider) == AiProvider.Gemini)
        {
            var gemini = _gemini.GetStatus();
            return View(Build(_settings.Current, gemini.CliFound, gemini.LoggedIn, gemini.Detail));
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

    /// <summary>Gemini sign-in state, in the same JSON shape as <see cref="ClaudeStatus"/> so the page script treats both alike.</summary>
    [HttpGet]
    public IActionResult GeminiStatus()
    {
        var s = _gemini.GetStatus();
        Response.Headers.CacheControl = "no-store";
        return Json(new { cliFound = s.CliFound, loggedIn = s.LoggedIn, detail = s.Detail, plan = (string?)null });
    }

    /// <summary>Opens Gemini's own "Login with Google" in a console window.</summary>
    [HttpPost]
    public IActionResult GeminiSignIn()
    {
        var error = _gemini.StartSignIn();
        return Json(new { ok = error == null, error });
    }

    [HttpPost]
    public IActionResult GeminiSignOut()
    {
        var error = _gemini.SignOut();
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
        var gemini = AiProvider.Normalize(settings.Provider) == AiProvider.Gemini;
        var checks = new List<SystemCheck>
        {
            gemini
                ? new SystemCheck("Gemini CLI", cliFound, _gemini.CliPath ?? "Not found. Install it with: npm install -g @google/gemini-cli")
                : new SystemCheck("Claude Code CLI", cliFound, _claude.CliPath ?? "Not found. Install it from https://claude.com/claude-code"),
            gemini
                ? new SystemCheck("Signed in to Gemini", signedIn, signedIn ? "Yes: " + detail : "Not signed in - use the button above, or set GEMINI_API_KEY.")
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
