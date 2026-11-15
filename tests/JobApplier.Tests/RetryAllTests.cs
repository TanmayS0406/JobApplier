using JobApplier.Controllers;
using JobApplier.Domain;
using JobApplier.Agents;
using JobApplier.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

public class LegacyLimitFailureTests
{
    private static JobStore Reload(TestEnv env, params JobRecord[] jobs)
    {
        var first = new JobStore(env.Paths, NullLogger<JobStore>.Instance);
        foreach (var job in jobs) first.Add(job);
        first.Flush();
        return new JobStore(env.Paths, NullLogger<JobStore>.Instance);   // loading is what upgrades old failures
    }

    private static JobRecord Failed(string detail, DateTime failedAt) => new() { Status = JobStatus.Failed, StatusDetail = detail, UpdatedUtc = failedAt };

    [Fact]
    public void An_old_failure_with_the_Claude_limit_message_becomes_a_limit_failure_with_its_reset_time()
    {
        using var env = new TestEnv();
        // Failed at 10:00 UTC on 10 Nov; "resets 8pm" in India is 20:00 IST = 14:30 UTC the same day.
        var failedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
        var job = Failed("You've hit your session limit · resets 8pm (Asia/Kolkata)", failedAt);

        var store = Reload(env, job);

        Assert.True(store.Read(job.Id, r => r.LimitHit));
        Assert.True(store.Read(job.Id, r => r.LimitResetKnown));
        Assert.Equal(new DateTime(2026, 11, 10, 14, 30, 0, DateTimeKind.Utc), store.Read(job.Id, r => r.RetryAfterUtc));
        Assert.False(store.Read(job.Id, r => r.AutoRetry));     // the user still chooses what happens
        Assert.Equal(JobStatus.Failed, store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public void A_reset_time_that_has_already_passed_means_the_job_can_be_retried_straight_away()
    {
        using var env = new TestEnv();
        var job = Failed("You've hit your session limit · resets 8pm (UTC)", DateTime.UtcNow.AddDays(-2));
        var store = Reload(env, job);
        var runner = new JobRunner(store, new HumanHelpBroker(store), new SettingsService(env.Paths),
            new PipelineServices(null!, store, new ProfileService(env.Paths), null!, env.Paths), NullLogger<JobRunner>.Instance);

        Assert.True(runner.ArmAutoRetry(job.Id));
        Assert.Equal(1, runner.RetryDueJobs(DateTime.UtcNow));
        Assert.Equal(JobStatus.Queued, store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public void A_limit_message_without_a_time_gets_an_estimate()
    {
        using var env = new TestEnv();
        var failedAt = DateTime.UtcNow.AddMinutes(-30);
        var job = Failed("API Error: 429 rate_limit_error", failedAt);

        var store = Reload(env, job);

        Assert.True(store.Read(job.Id, r => r.LimitHit));
        Assert.False(store.Read(job.Id, r => r.LimitResetKnown));
        Assert.Equal(failedAt.AddMinutes(3), store.Read(job.Id, r => r.RetryAfterUtc)!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Other_failures_and_other_states_are_left_alone()
    {
        using var env = new TestEnv();
        var crashed = Failed("The page crashed.", DateTime.UtcNow);
        var interrupted = new JobRecord { Status = JobStatus.Applying, StatusDetail = "rate limit" };   // not finished: becomes Interrupted, not a limit
        var applied = new JobRecord { Status = JobStatus.Applied, StatusDetail = "You've hit your session limit" };
        var already = Failed("x", DateTime.UtcNow);
        already.LimitHit = true;
        already.RetryAfterUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var store = Reload(env, crashed, interrupted, applied, already);

        Assert.False(store.Read(crashed.Id, r => r.LimitHit));
        Assert.False(store.Read(interrupted.Id, r => r.LimitHit));
        Assert.False(store.Read(applied.Id, r => r.LimitHit));
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), store.Read(already.Id, r => r.RetryAfterUtc)); // not recomputed
    }

    [Fact]
    public void The_upgrade_is_saved_so_it_happens_once()
    {
        using var env = new TestEnv();
        var job = Failed("You've hit your session limit · resets 8pm (UTC)", DateTime.UtcNow.AddDays(-1));
        Reload(env, job);   // the first load upgrades the job and marks it for saving

        var second = new JobStore(env.Paths, NullLogger<JobStore>.Instance);
        second.Flush();
        var third = new JobStore(env.Paths, NullLogger<JobStore>.Instance);

        Assert.True(third.Read(job.Id, r => r.LimitHit));
    }
}

public class RetryAllFailedTests
{
    private sealed class NoTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public void Every_failed_job_is_queued_again_and_nothing_else_is_touched()
    {
        using var rig = new Rig();
        var plain = new JobRecord { Status = JobStatus.Failed, Extracted = new ExtractedJob { Company = "A", Title = "t" } };
        var limited = rig.Limited(DateTime.UtcNow.AddHours(1), auto: true, retries: 2);
        var applied = new JobRecord { Status = JobStatus.Applied };
        var skipped = new JobRecord { Status = JobStatus.Skipped };
        var notFound = new JobRecord { Status = JobStatus.NotFound };
        foreach (var job in new[] { plain, applied, skipped, notFound }) rig.Store.Add(job);

        var queued = rig.Runner.RetryAllFailed();

        Assert.Equal(2, queued);
        Assert.Equal(JobStatus.Queued, rig.Store.Read(plain.Id, r => r.Status));
        Assert.Equal(JobStatus.Queued, rig.Store.Read(limited.Id, r => r.Status));
        Assert.False(rig.Store.Read(limited.Id, r => r.AutoRetry));                     // asking for everything now overrides the wait
        Assert.Equal(JobStatus.Applied, rig.Store.Read(applied.Id, r => r.Status));     // never submitted twice
        Assert.Equal(JobStatus.Skipped, rig.Store.Read(skipped.Id, r => r.Status));
        Assert.Equal(JobStatus.NotFound, rig.Store.Read(notFound.Id, r => r.Status));
    }

    [Fact]
    public void A_job_that_is_already_queued_is_not_queued_twice()
    {
        using var rig = new Rig();
        var failed = new JobRecord { Status = JobStatus.Failed };
        rig.Store.Add(failed);

        Assert.Equal(1, rig.Runner.RetryAllFailed());
        Assert.Equal(0, rig.Runner.RetryAllFailed());   // it is now queued, so it is no longer failed
        Assert.True(rig.Runner.IsActive(failed.Id));
    }

    [Fact]
    public void With_nothing_failed_nothing_happens()
    {
        using var rig = new Rig();
        rig.Store.Add(new JobRecord { Status = JobStatus.Applied });

        Assert.Equal(0, rig.Runner.RetryAllFailed());
    }

    [Fact]
    public void The_dashboard_button_reports_how_many_were_queued()
    {
        using var rig = new Rig();
        for (var i = 0; i < 3; i++) rig.Store.Add(new JobRecord { Status = JobStatus.Failed });
        var http = new DefaultHttpContext();
        var controller = new JobsController(rig.Store, rig.Runner, rig.Broker, rig.Profile, rig.Settings, rig.Env.Paths)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NoTempDataProvider())
        };

        controller.RetryAllFailed();

        Assert.Contains("Retrying 3 failed jobs", (string)controller.TempData["Message"]!);
        Assert.DoesNotContain(rig.Store.ReadAll(r => r.Status), s => s == JobStatus.Failed);
    }
}
