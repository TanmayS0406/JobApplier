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
            Live = LiveViewModelBuilder.Build(_store, _runner.PausedUntilUtc, _replies),
            Replies = RepliesViewModelBuilder.Build(_replies, _checker, _accounts),
            HasResume = _profile.HasResume && !string.IsNullOrWhiteSpace(profile.ResumeText),
            HasEmail = !string.IsNullOrWhiteSpace(profile.Email),
            ProfileEmail = profile.Email,
            DefaultAutoSubmit = settings.DefaultAutoSubmit,
            DefaultMinMatch = settings.DefaultMinMatchPercent,
            MaxParallel = settings.MaxParallelJobs,
            AccountsDocumentPath = _settings.AccountsDocumentPath
        });
    }

    /// <summary>The part of the dashboard that changes while jobs run (help requests + results table). Polled by the page.</summary>
    [HttpGet]
    public IActionResult Live()
    {
        var model = LiveViewModelBuilder.Build(_store, _runner.PausedUntilUtc, _replies);
        Response.Headers["X-Help-Count"] = model.Help.Count.ToString();
        Response.Headers.CacheControl = "no-store";
        return PartialView("_Live", model);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
