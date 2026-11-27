using System.Diagnostics;
using JobApplier.Mail;
using JobApplier.Models;
using JobApplier.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Controllers;

public class HomeController : Controller
{
    private readonly JobStore _store;
    private readonly ProfileService _profile;
    private readonly SettingsService _settings;
    private readonly JobRunner _runner;
    private readonly ReplyStore _replies;
    private readonly MailChecker _checker;
    private readonly AccountService _accounts;

    public HomeController(JobStore store, ProfileService profile, SettingsService settings, JobRunner runner,
                          ReplyStore replies, MailChecker checker, AccountService accounts)
    {
        _store = store;
        _profile = profile;
        _settings = settings;
        _runner = runner;
        _replies = replies;
        _checker = checker;
        _accounts = accounts;
    }

    public IActionResult Index()
    {
        var profile = _profile.Current;
        var settings = _settings.Current;
        return View(new DashboardViewModel
        {
            // Each list reads its own page, sort and filter from the dashboard's address (jobs_page, replies_sort, ...), so a reload shows the same page.
            Live = LiveViewModelBuilder.Build(_store, _runner.PausedUntilUtc, _replies, ListQuery.FromQuery(Request.Query, "jobs", ListQuery.JobSorts, ListQuery.JobShows), _runner.HeldByUser),
            Replies = RepliesViewModelBuilder.Build(_replies, _checker, _accounts, _store, ListQuery.FromQuery(Request.Query, "replies", ListQuery.ReplySorts, ListQuery.ReplyShows)),
            HasResume = _profile.HasResume && !string.IsNullOrWhiteSpace(profile.ResumeText),
            HasEmail = !string.IsNullOrWhiteSpace(profile.Email),
            ProfileEmail = profile.Email,
            DefaultAutoSubmit = settings.DefaultAutoSubmit,
            DefaultMinMatch = settings.DefaultMinMatchPercent,
            DefaultRequireVisaOrRelocation = settings.DefaultRequireVisaOrRelocation,
            MaxParallel = settings.MaxParallelJobs,
            AccountsDocumentPath = _settings.AccountsDocumentPath
        });
    }

    /// <summary>The part of the dashboard that changes while jobs run (help requests + results table). Polled by the page.</summary>
    [HttpGet]
    public IActionResult Live(int? page, int? size, string? sort, int? min, string? show = null)
    {
        var model = LiveViewModelBuilder.Build(_store, _runner.PausedUntilUtc, _replies, ListQuery.Create(page, size, sort, min, ListQuery.JobSorts, show, ListQuery.JobShows), _runner.HeldByUser);
        Response.Headers["X-Help-Count"] = model.Help.Count.ToString();
        Response.Headers.CacheControl = "no-store";
        return PartialView("_Live", model);
    }

    /// <summary>"Resume now": jobs held since the usage limit ("Pause when the usage limit ends" is on in Settings) start again.</summary>
    [HttpPost]
    public IActionResult Resume()
    {
        _runner.ResumeAfterLimit();
        return RedirectToAction(nameof(Index));
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
