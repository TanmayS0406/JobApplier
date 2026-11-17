using JobApplier.Agents;
using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace JobApplier.Tests;

public class RetryOptionsParsingTests
{
    [Fact]
    public void When_the_dialog_was_not_used_nothing_is_chosen_and_every_job_keeps_what_it_had()
    {
        Assert.Null(RetryOptions.From(chosen: false, autoSubmit: true, minMatch: 90));
    }

    [Theory]
    [InlineData(false, null, false, 0)]     // checkbox off and the number left empty
    [InlineData(true, 70, true, 70)]
    [InlineData(true, 150, true, 100)]      // out of range is pulled back in, never trusted
    [InlineData(true, -5, true, 0)]
    [InlineData(false, 100, false, 100)]
    public void The_choices_are_kept_within_range(bool autoSubmit, int? minMatch, bool expectedAuto, int expectedMin)
    {
        Assert.Equal(new RetryOptions(expectedAuto, expectedMin), RetryOptions.From(true, autoSubmit, minMatch));
    }

    [Fact]
    public void The_choices_are_described_in_plain_words()
    {
        Assert.Equal("submits automatically, skips below 70%", new RetryOptions(true, 70).Describe());
        Assert.Equal("waits for your approval before submitting, no minimum match", new RetryOptions(false, 0).Describe());
    }
}

public class RetryWithOptionsTests
{
    private static JobRecord Failed(Rig rig, bool auto = false, int min = 40, int? match = null, bool found = true)
    {
        var job = new JobRecord
        {
            Status = JobStatus.Failed,
            Options = new JobOptions { AutoSubmit = auto, MinMatchPercent = min },
            Extracted = new ExtractedJob { Company = "Acme", Title = "Dev" },
            Found = found ? new FoundPosting { Found = true, Url = "https://acme.example/jobs/1" } : null,
            Match = match is int p ? new MatchResult { SkillsScore = p, ExperienceScore = p, DomainScore = p, SeniorityScore = p, LogisticsScore = p } : null
        };
        rig.Store.Add(job);
        return job;
    }

    private static (bool Auto, int Min) Options(Rig rig, JobRecord job) => rig.Store.Read(job.Id, r => (r.Options.AutoSubmit, r.Options.MinMatchPercent));

    [Fact]
    public void A_retry_with_choices_replaces_the_jobs_settings_and_queues_it()
    {
        using var rig = new Rig();
        var job = Failed(rig, auto: false, min: 40);

        Assert.True(rig.Runner.Retry(job.Id, options: new RetryOptions(true, 75)));

        Assert.Equal((true, 75), Options(rig, job));
        Assert.Equal(JobStatus.Queued, rig.Store.Read(job.Id, r => r.Status));
        Assert.Contains(rig.Store.Read(job.Id, r => r.Log.Select(l => l.Text).ToList()), line => line.Contains("Retrying: submits automatically, skips below 75%"));
    }

    [Fact]
    public void A_retry_without_choices_leaves_the_jobs_settings_alone()
    {
        using var rig = new Rig();
        var job = Failed(rig, auto: true, min: 60);

        Assert.True(rig.Runner.Retry(job.Id));

        Assert.Equal((true, 60), Options(rig, job));
    }

    [Fact]
    public void Apply_anyway_still_wins_over_a_chosen_minimum_but_keeps_the_chosen_auto_submit()
    {
        using var rig = new Rig();
        var job = Failed(rig, auto: false, min: 40);

        rig.Runner.Retry(job.Id, applyAnyway: true, options: new RetryOptions(true, 90));

        Assert.Equal((true, 0), Options(rig, job));
    }

    [Fact]
    public void An_applied_job_is_never_retried_whatever_is_chosen()
    {
        using var rig = new Rig();
        var applied = new JobRecord { Status = JobStatus.Applied, Options = new JobOptions { AutoSubmit = false, MinMatchPercent = 10 } };
        rig.Store.Add(applied);

        Assert.False(rig.Runner.Retry(applied.Id, options: new RetryOptions(true, 99)));

        Assert.Equal((false, 10), Options(rig, applied));   // nothing was written onto a job that must not run again
    }

    [Fact]
    public void Retry_all_applies_the_choices_to_every_job_it_retries_and_to_nothing_else()
    {
        using var rig = new Rig();
        var one = Failed(rig, auto: false, min: 40);
        var two = Failed(rig, auto: true, min: 0);
        var applied = new JobRecord { Status = JobStatus.Applied, Options = new JobOptions { AutoSubmit = false, MinMatchPercent = 10 } };
        var skipped = new JobRecord { Status = JobStatus.Skipped, Options = new JobOptions { AutoSubmit = false, MinMatchPercent = 20 } };
        rig.Store.Add(applied);
        rig.Store.Add(skipped);

        var queued = rig.Runner.RetryAllFailed(new RetryOptions(true, 80));

        Assert.Equal(2, queued);
        Assert.Equal((true, 80), Options(rig, one));
        Assert.Equal((true, 80), Options(rig, two));
        Assert.Equal((false, 10), Options(rig, applied));
        Assert.Equal((false, 20), Options(rig, skipped));
    }

    [Fact]
    public async Task A_match_score_the_job_already_has_is_kept_and_judged_by_the_new_minimum()
    {
        using var rig = new Rig();
        var profile = ProfileService.Clone(rig.Profile.Current);
        profile.ResumeText = "My resume";
        rig.Profile.Save(profile);
        var job = Failed(rig, min: 70, match: 55);   // scored 55%, and the minimum was 70%
        var stage = new MatchExecutor(rig.Pipeline);

        var first = await stage.HandleAsync(new StageResult(job.Id, true), null!, CancellationToken.None);
        Assert.False(first.Continue);
        Assert.Equal(JobStatus.Skipped, rig.Store.Read(job.Id, r => r.Status));

        // The user lowers the minimum in the Retry dialog. The score is not asked for again (there is no agent here to ask)...
        Assert.True(rig.Runner.Retry(job.Id, options: new RetryOptions(false, 50)));
        var second = await stage.HandleAsync(new StageResult(job.Id, true), null!, CancellationToken.None);

        // ...and 55% now clears 50%, so the job goes on to be applied for.
        Assert.True(second.Continue);
        Assert.Equal(55, rig.Store.Read(job.Id, r => r.Match!.Percent));
    }

    [Fact]
    public async Task Raising_the_minimum_on_a_retry_skips_a_job_that_would_otherwise_have_gone_on()
    {
        using var rig = new Rig();
        var profile = ProfileService.Clone(rig.Profile.Current);
        profile.ResumeText = "My resume";
        rig.Profile.Save(profile);
        var job = Failed(rig, min: 0, match: 62);   // no minimum, so it got as far as applying and failed there
        rig.Runner.Retry(job.Id, options: new RetryOptions(true, 80));

        var result = await new MatchExecutor(rig.Pipeline).HandleAsync(new StageResult(job.Id, true), null!, CancellationToken.None);

        Assert.False(result.Continue);
        Assert.Equal(JobStatus.Skipped, rig.Store.Read(job.Id, r => r.Status));
        Assert.Contains("below your minimum of 80%", rig.Store.Read(job.Id, r => r.StatusDetail));
    }

    [Fact]
    public void Retry_when_available_saves_the_choices_now_so_the_automatic_retry_uses_them_later()
    {
        using var rig = new Rig();
        var job = rig.Limited(DateTime.UtcNow.AddMinutes(-10));   // its limit has already reset

        Assert.True(rig.Runner.ArmAutoRetry(job.Id, new RetryOptions(true, 80)));

        Assert.True(rig.Store.Read(job.Id, r => r.AutoRetry));
        Assert.Equal((true, 80), Options(rig, job));
        Assert.Equal(1, rig.Runner.RetryDueJobs(DateTime.UtcNow));    // the runner's own retry, later
        Assert.Equal((true, 80), Options(rig, job));                    // still what the user chose
        Assert.True(rig.Store.Read(job.Id, r => r.AutoRetry));          // an automatic retry keeps waiting in case the limit is still in force
    }

    [Fact]
    public void Retry_all_when_available_applies_the_choices_to_every_job_it_arms()
    {
        using var rig = new Rig();
        var one = rig.Limited(DateTime.UtcNow.AddHours(1));
        var two = rig.Limited(DateTime.UtcNow.AddHours(2));
        var already = rig.Limited(DateTime.UtcNow.AddHours(1), auto: true);   // already waiting: not touched again

        var armed = rig.Runner.ArmAllLimited(new RetryOptions(false, 65));

        Assert.Equal(2, armed);
        Assert.Equal((false, 65), Options(rig, one));
        Assert.Equal((false, 65), Options(rig, two));
        Assert.Equal((false, 0), Options(rig, already));
    }
}

public class RetryOptionsControllerTests
{
    private sealed class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static JobsController Controller(Rig rig)
    {
        var http = new DefaultHttpContext();
        return new JobsController(rig.Store, rig.Runner, rig.Broker, rig.Profile, rig.Settings, rig.Env.Paths)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NoTempData())
        };
    }

    private static JobRecord Failed(Rig rig, bool auto = false, int min = 40)
    {
        var job = new JobRecord { Status = JobStatus.Failed, Options = new JobOptions { AutoSubmit = auto, MinMatchPercent = min } };
        rig.Store.Add(job);
        return job;
    }

    private static (bool Auto, int Min) Options(Rig rig, JobRecord job) => rig.Store.Read(job.Id, r => (r.Options.AutoSubmit, r.Options.MinMatchPercent));

    [Fact]
    public void Retry_all_failed_with_the_dialogs_choices_applies_them_and_says_so()
    {
        using var rig = new Rig();
        var a = Failed(rig); var b = Failed(rig);
        var controller = Controller(rig);

        controller.RetryAllFailed(optionsChosen: true, autoSubmit: true, minMatch: 70);

        Assert.Equal((true, 70), Options(rig, a));
        Assert.Equal((true, 70), Options(rig, b));
        Assert.Contains("Retrying 2 failed jobs (submits automatically, skips below 70%)", (string)controller.TempData["Message"]!);
    }

    [Theory]
    [InlineData(null, 0)]    // the number was left empty
    [InlineData(999, 100)]
    [InlineData(-5, 0)]
    public void The_posted_minimum_is_pulled_back_into_range(int? posted, int expected)
    {
        using var rig = new Rig();
        var job = Failed(rig, min: 33);

        Controller(rig).RetryAllFailed(optionsChosen: true, autoSubmit: false, minMatch: posted);

        Assert.Equal((false, expected), Options(rig, job));
    }

    [Fact]
    public void Without_the_dialog_the_old_buttons_still_work_and_change_nothing()
    {
        using var rig = new Rig();
        var job = Failed(rig, auto: true, min: 55);
        var controller = Controller(rig);

        controller.RetryAllFailed();

        Assert.Equal((true, 55), Options(rig, job));
        Assert.Equal("Retrying 1 failed job. They run 3 at a time.", (string)controller.TempData["Message"]!);
    }

    [Fact]
    public void Retrying_one_job_with_the_dialogs_choices_applies_them_and_returns_to_its_page()
    {
        using var rig = new Rig();
        var job = Failed(rig, auto: true, min: 80);

        var result = Controller(rig).Retry(job.Id, applyAnyway: false, optionsChosen: true, autoSubmit: false, minMatch: 55);

        Assert.Equal((false, 55), Options(rig, job));
        Assert.Equal("Details", Assert.IsType<RedirectToActionResult>(result).ActionName);
    }

    [Fact]
    public void Retry_when_available_for_one_job_saves_the_choices_and_says_what_will_happen()
    {
        using var rig = new Rig();
        var job = rig.Limited(DateTime.UtcNow.AddHours(1));
        var controller = Controller(rig);

        controller.RetryWhenAvailable(job.Id, optionsChosen: true, autoSubmit: true, minMatch: 65);

        Assert.True(rig.Store.Read(job.Id, r => r.AutoRetry));
        Assert.Equal((true, 65), Options(rig, job));
        Assert.Contains("(submits automatically, skips below 65%)", (string)controller.TempData["Message"]!);
    }

    [Fact]
    public void Retry_all_when_available_applies_the_choices_to_every_waiting_job()
    {
        using var rig = new Rig();
        var one = rig.Limited(DateTime.UtcNow.AddHours(1));
        var two = rig.Limited(DateTime.UtcNow.AddHours(1));
        var controller = Controller(rig);

        controller.RetryAllWhenAvailable(optionsChosen: true, autoSubmit: false, minMatch: 90);

        Assert.Equal((false, 90), Options(rig, one));
        Assert.Equal((false, 90), Options(rig, two));
        Assert.Contains("2 jobs will be retried automatically", (string)controller.TempData["Message"]!);
        Assert.Contains("skips below 90%", (string)controller.TempData["Message"]!);
    }
}
