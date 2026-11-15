using JobApplier.Domain;
using JobApplier.Infrastructure;
using JobApplier.Models;
using JobApplier.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobApplier.Controllers;

public class JobsController : Controller
{
    private const int MaxFiles = 30;
    private const long MaxFileBytes = 20L * 1024 * 1024;

    private static readonly HashSet<string> HelpActions = new() { "done", "answer", "yes", "approve", "submitted", "skip" };

    private readonly JobStore _store;
    private readonly JobRunner _runner;
    private readonly HumanHelpBroker _help;
    private readonly ProfileService _profile;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;

    public JobsController(JobStore store, JobRunner runner, HumanHelpBroker help, ProfileService profile, SettingsService settings, AppPaths paths)
    {
        _store = store;
        _runner = runner;
        _help = help;
        _profile = profile;
        _settings = settings;
        _paths = paths;
    }

    /// <summary>
    /// One job per screenshot, all queued and run in parallel up to the configured limit. A screenshot that lists several jobs is
    /// split into one job per posting once it has been read (see <c>ExtractExecutor</c>), so the number of jobs grows as they start.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Upload(List<IFormFile> screenshots, bool autoSubmit, int minMatch, CancellationToken cancellationToken)
    {
        var profile = _profile.Current;
        if (!_profile.HasResume || string.IsNullOrWhiteSpace(profile.ResumeText) || string.IsNullOrWhiteSpace(profile.Email))
        {
            TempData["Error"] = "Add your resume on the Profile page first - the e-mail address on it is used for every account.";
            return RedirectToAction("Index", "Profile");
        }

        var files = screenshots.Where(f => f.Length > 0).ToList();
        if (files.Count == 0)
        {
            TempData["Error"] = "Choose at least one screenshot.";
            return RedirectToAction("Index", "Home");
        }
        if (files.Count > MaxFiles)
        {
            TempData["Error"] = $"Upload at most {MaxFiles} screenshots at a time.";
            return RedirectToAction("Index", "Home");
        }

        var options = new JobOptions { AutoSubmit = autoSubmit, MinMatchPercent = Math.Clamp(minMatch, 0, 100) };
        var started = 0;
        var rejected = new List<string>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file.FileName);
            if (file.Length > MaxFileBytes) { rejected.Add($"{name} (larger than 20 MB)"); continue; }

            var header = new byte[16];
            int read;
            await using (var peek = file.OpenReadStream()) read = await peek.ReadAsync(header, cancellationToken);
            var extension = ImageSniffer.Detect(header.AsSpan(0, read));
            if (extension == null) { rejected.Add($"{name} (not a PNG, JPG, GIF or WebP image)"); continue; }

            var job = new JobRecord { Options = new JobOptions { AutoSubmit = options.AutoSubmit, MinMatchPercent = options.MinMatchPercent }, OriginalFileName = name };
            job.ScreenshotFile = "screenshot" + extension;
            Directory.CreateDirectory(_paths.JobFolder(job.Id));
            await using (var target = System.IO.File.Create(Path.Combine(_paths.JobFolder(job.Id), job.ScreenshotFile)))
            await using (var source = file.OpenReadStream())
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            _store.Add(job);
            _runner.Enqueue(job.Id);
            started++;
        }

        if (started > 0)
            TempData["Message"] = $"Started {started} screenshot{(started == 1 ? "" : "s")}. A screenshot that lists several jobs adds one job for each as it is read; " +
                                  "browser windows will open as they get to the applications.";
        if (rejected.Count > 0) TempData["Error"] = "Skipped: " + string.Join("; ", rejected) + ".";
        return RedirectToAction("Index", "Home");
    }

    public IActionResult Details(Guid id)
    {
        var model = BuildDetails(id);
        return model == null ? NotFound() : View(model);
    }

    [HttpGet]
    public IActionResult Body(Guid id)
    {
        var model = BuildDetails(id);
        if (model == null) return NotFound();
        Response.Headers["X-Help-Count"] = model.Job.PendingHelp == null ? "0" : "1";
        Response.Headers.CacheControl = "no-store";
        return PartialView("_JobBody", model);
    }

    [HttpGet]
    public IActionResult Screenshot(Guid id)
    {
        var file = _store.Read(id, r => r.ScreenshotFile);
        if (string.IsNullOrEmpty(file)) return NotFound();

        var path = Path.Combine(_paths.JobFolder(id), file);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, ImageSniffer.ContentType(Path.GetExtension(file)));
    }

    /// <summary>The user's response to a captcha / verification / question / review request.</summary>
    [HttpPost]
    public IActionResult Help(Guid id, string requestId, string action, string? text)
    {
        if (!HelpActions.Contains(action)) return BadRequest("Unknown action.");
        text = (text ?? "").Trim();
        if (action == "answer" && text.Length == 0) return BadRequest("Type an answer first.");

        var request = _help.Resolve(id, requestId, new HelpResponse(action, text));
        if (request == null) return Conflict("That request is no longer waiting.");

        // Questions are asked once: the answer is kept and given to every later application automatically.
        if (HelpPolicy.ShouldRemember(request, action)) _profile.AddAnswer(request.Message, text);

        return IsFetch() ? Ok() : RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public IActionResult Retry(Guid id, bool applyAnyway)
    {
        if (!_runner.Retry(id, applyAnyway)) TempData["Error"] = "That job cannot be retried right now.";
        return IsFetch() ? Ok() : RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>For a job that stopped on a usage limit: retry by itself once the limit has reset.</summary>
    [HttpPost]
    public IActionResult RetryWhenAvailable(Guid id)
    {
        if (_runner.ArmAutoRetry(id)) TempData["Message"] = "OK. This job will be retried automatically once the usage limit has reset.";
        else TempData["Error"] = "That job is not waiting on a usage limit.";
        return IsFetch() ? Ok() : RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Stops waiting for the limit to reset; the job stays failed.</summary>
    [HttpPost]
    public IActionResult StopWaiting(Guid id)
    {
        _runner.DisarmAutoRetry(id);
        return IsFetch() ? Ok() : RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>The dashboard button: retry every failed job now.</summary>
    [HttpPost]
    public IActionResult RetryAllFailed()
    {
        var queued = _runner.RetryAllFailed();
        TempData["Message"] = queued == 0
            ? "There were no failed jobs to retry."
            : $"Retrying {queued} failed job{(queued == 1 ? "" : "s")}. They run {_settings.Current.MaxParallelJobs} at a time.";
        return RedirectToAction("Index", "Home");
    }

    /// <summary>The dashboard button: every job that stopped on a usage limit will retry by itself once it has reset.</summary>
    [HttpPost]
    public IActionResult RetryAllWhenAvailable()
    {
        var armed = _runner.ArmAllLimited();
        TempData["Message"] = armed == 0
            ? "No jobs are waiting on a usage limit."
            : $"{armed} job{(armed == 1 ? "" : "s")} will be retried automatically once the usage limit has reset.";
        return RedirectToAction("Index", "Home");
    }

    [HttpPost]
    public IActionResult Cancel(Guid id)
    {
        _runner.Cancel(id);
        return IsFetch() ? Ok() : RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public IActionResult Delete(Guid id)
    {
        if (_runner.IsActive(id))
        {
            TempData["Error"] = "Cancel the job before deleting it.";
            return RedirectToAction(nameof(Details), new { id });
        }
        _store.Remove(id);
        return RedirectToAction("Index", "Home");
    }

    /// <summary>
    /// Deletes every job that ended as Failed, with its files. Jobs that are still running or queued are never touched, and
    /// neither are the accounts and the applications list: only the job records go.
    /// </summary>
    [HttpPost]
    public IActionResult ClearFailed()
    {
        // A job waiting for a usage limit to reset is not abandoned: it will retry by itself, so it is never cleared.
        var removed = _store.RemoveWhere(r => r.Status == JobStatus.Failed && !r.WaitingForLimitReset && !_runner.IsActive(r.Id));
        TempData["Message"] = removed == 0
            ? "There were no failed jobs to clear."
            : $"Cleared {removed} failed job{(removed == 1 ? "" : "s")}.";
        return RedirectToAction("Index", "Home");
    }

    private bool IsFetch() => Request.Headers["X-Requested-With"] == "fetch";

    private JobDetailsViewModel? BuildDetails(Guid id)
    {
        // Copy under the job's lock: the view must not enumerate lists that a worker is still appending to.
        var snapshot = _store.Read(id, r => new JobRecord
        {
            Id = r.Id,
            CreatedUtc = r.CreatedUtc,
            UpdatedUtc = r.UpdatedUtc,
            ScreenshotFile = r.ScreenshotFile,
            OriginalFileName = r.OriginalFileName,
            Status = r.Status,
            StatusDetail = r.StatusDetail,
            Options = new JobOptions { AutoSubmit = r.Options.AutoSubmit, MinMatchPercent = r.Options.MinMatchPercent },
            Extracted = r.Extracted,
            Found = r.Found,
            Match = r.Match,
            Apply = r.Apply,
            PendingHelp = r.PendingHelp,
            AccountRecorded = r.AccountRecorded,
            LimitHit = r.LimitHit,
            RetryAfterUtc = r.RetryAfterUtc,
            LimitResetKnown = r.LimitResetKnown,
            AutoRetry = r.AutoRetry,
            LimitRetries = r.LimitRetries,
            Log = r.Log.ToList()
        });
        if (snapshot == null) return null;

        return new JobDetailsViewModel
        {
            Job = snapshot,
            Company = snapshot.DisplayCompany,
            Title = snapshot.DisplayTitle,
            IsActive = _runner.IsActive(id),
            ProfileEmail = _profile.Current.Email,
            AccountsDocumentPath = _settings.AccountsDocumentPath,
            HasScreenshot = !string.IsNullOrEmpty(snapshot.ScreenshotFile)
        };
    }
}
