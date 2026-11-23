using JobApplier.Domain;
using JobApplier.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobApplier.Tests;

internal sealed class FakeLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _stopping = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => _stopping.Cancel();
}

internal static class RunnerTestExtensions
{
    /// <summary>A runner over the rig's store and queue (what the app has after it is started again).</summary>
    public static JobRunner NewRunner(this Rig rig, IHostApplicationLifetime? lifetime = null) =>
        new(rig.Store, rig.Broker, rig.Settings, rig.Pipeline, rig.Queue, NullLogger<JobRunner>.Instance, lifetime);

    public static async Task<bool> Completes(this Task task, int milliseconds = 5000) => await Task.WhenAny(task, Task.Delay(milliseconds)) == task;

    public static async Task<bool> Eventually(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}

/// <summary>Jobs go through the durable queue: queued, run, held back by a usage limit, cancelled, interrupted and brought back.</summary>
public class DurableQueueTests
{
    private static JobRecord NewJob(Rig rig, JobStatus status = JobStatus.Queued, DateTime? created = null)
    {
        var job = new JobRecord
        {
            Status = status,
            CreatedUtc = created ?? DateTime.UtcNow,
            Extracted = new ExtractedJob { Company = "Acme", Title = "Dev" }
        };
        rig.Store.Add(job);
        return job;
    }

    private static string Detail(Rig rig, Guid id) => rig.Store.Read(id, r => r.StatusDetail)!;
    private static List<string> LogOf(Rig rig, Guid id) => rig.Store.Read(id, r => r.Log.Select(l => l.Text).ToList())!;

    // ---- queuing ----

    [Fact]
    public void A_job_is_marked_queued_before_the_queue_hears_of_it_so_a_fast_worker_is_not_overwritten()
    {
        using var rig = new Rig();
        var job = NewJob(rig, JobStatus.Failed);
        JobStatus? seen = null;
        rig.Queue.OnEnqueue = id => seen = rig.Store.Read(id, r => r.Status);

        Assert.True(rig.Runner.Retry(job.Id));

        Assert.Equal(JobStatus.Queued, seen);
    }

    [Fact]
    public void Queuing_remembers_the_queue_entry_and_a_job_is_not_queued_twice()
    {
        using var rig = new Rig();
        var job = NewJob(rig);

        Assert.True(rig.Runner.Enqueue(job.Id));
        Assert.False(rig.Runner.Enqueue(job.Id));

        var entry = Assert.Single(rig.Queue.Enqueued);
        Assert.Equal(job.Id, entry.JobId);
        Assert.Equal(entry.Id, rig.Store.Read(job.Id, r => r.QueueId));
        Assert.True(rig.Runner.IsActive(job.Id));
        Assert.False(rig.Runner.Enqueue(Guid.NewGuid()));   // not a job
    }

    [Fact]
    public void A_queue_that_cannot_be_reached_fails_the_job_so_it_can_be_retried_instead_of_losing_it()
    {
        using var rig = new Rig();
        var job = NewJob(rig);
        rig.Queue.FailWith = new InvalidOperationException("the database is not reachable");

        Assert.False(rig.Runner.Enqueue(job.Id));

        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
        Assert.Contains("the database is not reachable", Detail(rig, job.Id));
        Assert.False(rig.Runner.IsActive(job.Id));

        rig.Queue.FailWith = null;
        Assert.True(rig.Runner.Retry(job.Id));
        Assert.True(rig.Runner.IsActive(job.Id));
    }

    // ---- running ----

    [Fact]
    public async Task A_queued_job_runs_when_its_turn_comes_and_is_cleared_afterwards()
    {
        using var rig = new Rig();
        var job = NewJob(rig);
        rig.Runner.RunWorkflow = (id, _) => { rig.Store.SetStatus(id, JobStatus.Applied, "done"); return Task.CompletedTask; };
        rig.Runner.Enqueue(job.Id);

        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Applied, rig.Store.Read(job.Id, r => r.Status));
        Assert.False(rig.Runner.IsActive(job.Id));
        Assert.Contains("Started.", LogOf(rig, job.Id));
    }

    [Fact]
    public async Task A_queue_entry_that_has_nothing_to_do_does_nothing()
    {
        using var rig = new Rig();
        var runs = 0;
        rig.Runner.RunWorkflow = (_, _) => { runs++; return Task.CompletedTask; };
        var job = NewJob(rig);

        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);          // never queued by this runner
        await rig.Runner.RunQueuedJobAsync(Guid.NewGuid(), CancellationToken.None);  // not a job at all

        rig.Runner.Enqueue(job.Id);
        rig.Store.Remove(job.Id);
        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);          // deleted since it was queued

        Assert.Equal(0, runs);
        Assert.False(rig.Runner.IsActive(job.Id));
    }

    [Fact]
    public async Task A_job_that_is_already_finished_is_not_run_again_by_a_late_queue_entry()
    {
        using var rig = new Rig();
        var runs = 0;
        rig.Runner.RunWorkflow = (id, _) => { runs++; return Task.CompletedTask; };
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);
        rig.Store.SetStatus(job.Id, JobStatus.Applied, "done elsewhere");

        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(0, runs);
        Assert.Equal(JobStatus.Applied, rig.Store.Read(job.Id, r => r.Status));
        Assert.False(rig.Runner.IsActive(job.Id));
    }

    [Fact]
    public async Task Two_deliveries_of_the_same_job_run_it_once()
    {
        using var rig = new Rig();
        var runs = 0;
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.Runner.RunWorkflow = async (id, _) =>
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await release.Task;
            rig.Store.SetStatus(id, JobStatus.Applied, "done");
        };
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);

        var first = rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);
        await started.Task;
        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);   // the second delivery (an entry left over from before a restart)
        release.SetResult();
        await first;

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_workflow_that_crashes_leaves_a_failed_job_not_a_stuck_one()
    {
        using var rig = new Rig();
        rig.Runner.RunWorkflow = (_, _) => throw new InvalidOperationException("boom");
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);

        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
        Assert.Contains("boom", Detail(rig, job.Id));
        Assert.False(rig.Runner.IsActive(job.Id));
    }

    [Fact]
    public async Task A_workflow_that_ends_without_a_result_does_not_leave_the_job_running()
    {
        using var rig = new Rig();
        rig.Runner.RunWorkflow = (id, _) => { rig.Store.SetStatus(id, JobStatus.Applying, "mid-way"); return Task.CompletedTask; };
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);

        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
        Assert.Contains("Stopped unexpectedly", Detail(rig, job.Id));
    }

    // ---- cancelling ----

    [Fact]
    public async Task Cancelling_a_job_that_is_still_waiting_takes_it_out_of_the_queue()
    {
        using var rig = new Rig();
        var runs = 0;
        rig.Runner.RunWorkflow = (_, _) => { runs++; return Task.CompletedTask; };
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);
        var entry = rig.Queue.Enqueued.Single().Id;

        rig.Runner.Cancel(job.Id);

        Assert.Contains(entry, rig.Queue.Deleted);
        Assert.Equal(JobStatus.Cancelled, rig.Store.Read(job.Id, r => r.Status));
        Assert.False(rig.Runner.IsActive(job.Id));

        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);   // the entry, should it still fire
        Assert.Equal(0, runs);
        Assert.Equal(JobStatus.Cancelled, rig.Store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public async Task Cancelling_a_running_job_stops_it()
    {
        using var rig = new Rig();
        var started = new TaskCompletionSource();
        rig.Runner.RunWorkflow = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);
        var run = rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);
        await started.Task;

        rig.Runner.Cancel(job.Id);

        Assert.True(await run.Completes());
        Assert.Equal(JobStatus.Cancelled, rig.Store.Read(job.Id, r => r.Status));
        Assert.False(rig.Runner.IsActive(job.Id));
    }

    [Fact]
    public async Task A_cancelled_job_can_be_retried_and_then_runs()
    {
        using var rig = new Rig();
        rig.Runner.RunWorkflow = (id, _) => { rig.Store.SetStatus(id, JobStatus.Applied, "done"); return Task.CompletedTask; };
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);
        rig.Runner.Cancel(job.Id);

        Assert.True(rig.Runner.Retry(job.Id));
        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Applied, rig.Store.Read(job.Id, r => r.Status));
    }

    // ---- the app being closed ----

    [Fact]
    public async Task Closing_the_app_leaves_a_running_job_queued_to_continue_not_cancelled()
    {
        using var rig = new Rig();
        var lifetime = new FakeLifetime();
        var runner = rig.NewRunner(lifetime);
        var started = new TaskCompletionSource();
        runner.RunWorkflow = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var job = NewJob(rig, JobStatus.Searching);
        runner.Enqueue(job.Id);
        using var shutdown = new CancellationTokenSource();
        var run = runner.RunQueuedJobAsync(job.Id, shutdown.Token);
        await started.Task;

        lifetime.StopApplication();
        shutdown.Cancel();

        Assert.True(await run.Completes());
        Assert.Equal(JobStatus.Queued, rig.Store.Read(job.Id, r => r.Status));
        Assert.Contains("JobApplier was closed", Detail(rig, job.Id));
    }

    [Fact]
    public async Task A_stopped_agent_during_shutdown_is_a_pause_not_a_failure()
    {
        // When the app closes, the Claude process is killed first; the stage then sees an agent that died and fails the job.
        using var rig = new Rig();
        var lifetime = new FakeLifetime();
        var runner = rig.NewRunner(lifetime);
        runner.RunWorkflow = (id, _) =>
        {
            lifetime.StopApplication();
            rig.Store.SetStatus(id, JobStatus.Failed, "The Claude process ended unexpectedly");
            return Task.CompletedTask;
        };
        var job = NewJob(rig, JobStatus.Applying);
        runner.Enqueue(job.Id);

        await runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Queued, rig.Store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public async Task A_real_failure_while_the_app_is_open_stays_failed()
    {
        using var rig = new Rig();
        var runner = rig.NewRunner(new FakeLifetime());
        runner.RunWorkflow = (id, _) => { rig.Store.SetStatus(id, JobStatus.Failed, "The site rejected the form"); return Task.CompletedTask; };
        var job = NewJob(rig);
        runner.Enqueue(job.Id);

        await runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
        Assert.False(rig.Store.Read(job.Id, r => r.AutoRetry));
    }

    [Fact]
    public async Task Pressing_cancel_while_the_app_closes_is_still_a_cancel()
    {
        using var rig = new Rig();
        var lifetime = new FakeLifetime();
        var runner = rig.NewRunner(lifetime);
        var started = new TaskCompletionSource();
        runner.RunWorkflow = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var job = NewJob(rig);
        runner.Enqueue(job.Id);
        var run = runner.RunQueuedJobAsync(job.Id, CancellationToken.None);
        await started.Task;

        runner.Cancel(job.Id);
        lifetime.StopApplication();

        Assert.True(await run.Completes());
        Assert.Equal(JobStatus.Cancelled, rig.Store.Read(job.Id, r => r.Status));
    }

    // ---- coming back after a restart ----

    [Fact]
    public void After_a_restart_every_unfinished_job_is_queued_again_oldest_first_and_nothing_else_is()
    {
        using var rig = new Rig();
        var t0 = DateTime.UtcNow.AddHours(-3);
        var midway = NewJob(rig, JobStatus.Applying, t0);
        rig.Store.Update(midway.Id, r => r.QueueId = "old-1");
        var waiting = NewJob(rig, JobStatus.Queued, t0.AddMinutes(10));
        rig.Store.Update(waiting.Id, r => r.QueueId = "old-2");
        var finished = NewJob(rig, JobStatus.Applied, t0.AddMinutes(20));
        var failed = NewJob(rig, JobStatus.Failed, t0.AddMinutes(30));
        var limited = rig.Limited(DateTime.UtcNow.AddHours(1), auto: true);
        rig.Store.Flush();

        // A new process: nothing in memory, only the database.
        var restarted = new Rig(rig.Env);
        var queued = restarted.Runner.RecoverAfterRestart();

        Assert.Equal(2, queued);
        Assert.Equal(new[] { midway.Id, waiting.Id }, restarted.Queue.Enqueued.Select(e => e.JobId));    // oldest first, once each
        Assert.Equal(new[] { "old-1", "old-2" }, restarted.Queue.Deleted);                               // what the last run left behind is removed
        Assert.All(new[] { midway.Id, waiting.Id }, id => Assert.Equal(JobStatus.Queued, restarted.Store.Read(id, r => r.Status)));
        Assert.Contains(restarted.Store.Read(midway.Id, r => r.Log.Select(l => l.Text).ToList())!, t => t.Contains("closed") && t.Contains("Filling the application"));
        Assert.Equal(JobStatus.Applied, restarted.Store.Read(finished.Id, r => r.Status));
        Assert.Equal(JobStatus.Failed, restarted.Store.Read(failed.Id, r => r.Status));
        Assert.False(restarted.Runner.IsActive(limited.Id));                                             // waiting for its limit, not queued
        Assert.DoesNotContain(restarted.Queue.Enqueued, e => e.JobId == limited.Id);
    }

    [Fact]
    public async Task A_job_interrupted_in_the_middle_continues_from_the_stage_it_had_reached()
    {
        // The real pipeline, with the stages that finished before the app was closed stored on the job. The agent factory is missing
        // on purpose: reading the screenshot, finding the posting or scoring again would need it and fail.
        using var first = new Rig();
        var job = new JobRecord
        {
            Status = JobStatus.Applying,
            Extracted = new ExtractedJob { Company = "Acme", Title = "Dev", Description = "d" },
            Found = new FoundPosting { Found = true, Url = "https://acme.example/jobs/1", Company = "Acme", Title = "Dev" },
            Match = new MatchResult { SkillsScore = 90, ExperienceScore = 90, DomainScore = 90, SeniorityScore = 90, LogisticsScore = 90 }
        };
        first.Store.Add(job);
        first.Store.Flush();

        using var restarted = new Rig(first.Env);
        restarted.Profile.Save(new UserProfile { ResumeText = "My resume", Email = "" });   // the apply stage is the first to need something
        restarted.Runner.RecoverAfterRestart();

        await restarted.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        var detail = restarted.Store.Read(job.Id, r => r.StatusDetail)!;
        Assert.Equal(JobStatus.Failed, restarted.Store.Read(job.Id, r => r.Status));
        Assert.Contains("no e-mail address", detail);                  // it got as far as the apply stage ...
        Assert.DoesNotContain("Unexpected error", detail);              // ... without redoing a finished stage
        Assert.DoesNotContain(LogOf(restarted, job.Id), t => t.StartsWith("Read the screenshot"));
        Assert.Equal(90, restarted.Store.Read(job.Id, r => r.Match!.Percent));
    }

    // ---- usage limits ----

    [Fact]
    public async Task A_job_that_hits_the_usage_limit_waits_for_the_reset_and_comes_back_by_itself()
    {
        using var rig = new Rig();
        var now = DateTime.UtcNow;
        rig.Runner.Clock = () => now;
        var reset = now.AddHours(2);
        var attempt = 0;
        rig.Runner.RunWorkflow = (id, _) =>
        {
            if (++attempt == 1)
            {
                // What a stage does when the CLI reports that the tokens are used up.
                rig.Store.Update(id, r => { r.LimitHit = true; r.RetryAfterUtc = reset; r.LimitResetKnown = true; });
                rig.Store.SetStatus(id, JobStatus.Failed, "The AI usage limit was reached");
                rig.Runner.NoteUsageLimit(reset);
            }
            else
            {
                rig.Store.SetStatus(id, JobStatus.Applied, "done");
            }
            return Task.CompletedTask;
        };
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);

        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        // It is waiting without anyone pressing "retry when available".
        Assert.Equal(JobStatus.Failed, rig.Store.Read(job.Id, r => r.Status));
        Assert.True(rig.Store.Read(job.Id, r => r.WaitingForLimitReset));
        Assert.Contains(LogOf(rig, job.Id), t => t.Contains("Will retry automatically"));
        Assert.False(rig.Runner.IsActive(job.Id));

        // While the limit is in force, the check leaves it alone ...
        now = reset.AddMinutes(-30);
        Assert.Equal(0, rig.Runner.RetryDueJobs(now));
        Assert.Single(rig.Queue.Enqueued);

        // ... and once it has reset, the recurring check puts it back in the queue.
        now = reset.AddMinutes(5);
        rig.Runner.CheckDueRetries();
        Assert.Equal(2, rig.Queue.Enqueued.Count);
        Assert.Equal(JobStatus.Queued, rig.Store.Read(job.Id, r => r.Status));
        Assert.Equal(1, rig.Store.Read(job.Id, r => r.LimitRetries));

        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Applied, rig.Store.Read(job.Id, r => r.Status));
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task A_limit_that_is_still_in_force_when_the_job_comes_back_keeps_it_waiting()
    {
        using var rig = new Rig();
        var now = DateTime.UtcNow;
        rig.Runner.Clock = () => now;
        var attempts = 0;
        rig.Runner.RunWorkflow = (id, _) =>
        {
            attempts++;
            var next = now.AddHours(1);     // the CLI says "try again in an hour" every time
            rig.Store.Update(id, r => { r.LimitHit = true; r.RetryAfterUtc = next; r.LimitResetKnown = true; });
            rig.Store.SetStatus(id, JobStatus.Failed, "The AI usage limit was reached");
            rig.Runner.NoteUsageLimit(next);
            return Task.CompletedTask;
        };
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);
        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        for (var round = 1; round <= 3; round++)
        {
            now = now.AddHours(1).AddMinutes(5);
            rig.Runner.CheckDueRetries();
            await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);
        }

        Assert.Equal(4, attempts);
        Assert.True(rig.Store.Read(job.Id, r => r.WaitingForLimitReset));
        Assert.Equal(3, rig.Store.Read(job.Id, r => r.LimitRetries));
    }

    [Fact]
    public async Task A_job_that_starts_while_the_limit_is_in_force_is_put_back_for_after_it()
    {
        using var rig = new Rig();
        var now = DateTime.UtcNow;
        rig.Runner.Clock = () => now;
        var runs = 0;
        rig.Runner.RunWorkflow = (id, _) => { runs++; rig.Store.SetStatus(id, JobStatus.Applied, "done"); return Task.CompletedTask; };
        rig.Runner.NoteUsageLimit(now.AddHours(1));
        var hold = rig.Runner.PausedUntilUtc!.Value;
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);

        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(0, runs);
        var scheduled = Assert.Single(rig.Queue.Scheduled);
        Assert.Equal(job.Id, scheduled.JobId);
        Assert.Equal(hold, scheduled.RunAtUtc);
        Assert.Equal(scheduled.Id, rig.Store.Read(job.Id, r => r.QueueId));
        Assert.Equal(JobStatus.Queued, rig.Store.Read(job.Id, r => r.Status));
        Assert.Contains("usage limit", Detail(rig, job.Id));
        Assert.True(rig.Runner.IsActive(job.Id));                        // still the runner's to run

        // The scheduled entry fires after the hold: now it runs.
        now = hold.AddSeconds(1);
        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);

        Assert.Equal(1, runs);
        Assert.Equal(JobStatus.Applied, rig.Store.Read(job.Id, r => r.Status));
    }

    [Fact]
    public async Task A_job_put_back_for_the_limit_can_still_be_cancelled()
    {
        using var rig = new Rig();
        rig.Runner.NoteUsageLimit(DateTime.UtcNow.AddHours(1));
        var job = NewJob(rig);
        rig.Runner.Enqueue(job.Id);
        await rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None);
        var scheduled = rig.Queue.Scheduled.Single().Id;

        rig.Runner.Cancel(job.Id);

        Assert.Contains(scheduled, rig.Queue.Deleted);
        Assert.Equal(JobStatus.Cancelled, rig.Store.Read(job.Id, r => r.Status));
        Assert.False(rig.Runner.IsActive(job.Id));
    }

    [Fact]
    public void After_a_restart_a_usage_limit_that_is_still_in_force_still_holds_new_jobs_back()
    {
        using var rig = new Rig();
        rig.Limited(DateTime.UtcNow.AddHours(2), auto: true);
        rig.Store.Flush();

        using var restarted = new Rig(rig.Env);
        Assert.Null(restarted.Runner.PausedUntilUtc);

        restarted.Runner.RecoverAfterRestart();

        Assert.NotNull(restarted.Runner.PausedUntilUtc);
        Assert.InRange(restarted.Runner.PausedUntilUtc!.Value, DateTime.UtcNow.AddHours(2), DateTime.UtcNow.AddHours(2).AddMinutes(2));
    }

    [Fact]
    public void A_limit_that_ended_while_the_app_was_closed_holds_nothing_and_the_job_is_retried_at_once()
    {
        using var rig = new Rig();
        var job = rig.Limited(DateTime.UtcNow.AddHours(-3), auto: true, updated: DateTime.UtcNow.AddHours(-4));
        rig.Store.Flush();

        using var restarted = new Rig(rig.Env);
        restarted.Runner.RecoverAfterRestart();

        Assert.Null(restarted.Runner.PausedUntilUtc);
        Assert.Equal(1, restarted.Runner.RetryDueJobs(DateTime.UtcNow));
        Assert.Equal(JobStatus.Queued, restarted.Store.Read(job.Id, r => r.Status));
    }

    // ---- how many run at once ----

    [Fact]
    public async Task No_more_jobs_run_at_once_than_the_setting_allows_and_a_change_applies_without_a_restart()
    {
        using var rig = new Rig();
        rig.Settings.Save(new AppSettings { MaxParallelJobs = 2, AccountsDocumentPath = Path.Combine(rig.Env.Root, "a.docx") });
        var running = 0;
        var peak = 0;
        var startOrder = new List<Guid>();
        var gates = new Dictionary<Guid, TaskCompletionSource>();
        rig.Runner.RunWorkflow = async (id, _) =>
        {
            lock (startOrder) { startOrder.Add(id); running++; peak = Math.Max(peak, running); }
            await gates[id].Task;
            lock (startOrder) running--;
            rig.Store.SetStatus(id, JobStatus.Applied, "done");
        };

        var jobs = Enumerable.Range(0, 4).Select(i => NewJob(rig, JobStatus.Queued, DateTime.UtcNow.AddSeconds(i))).ToList();
        foreach (var job in jobs) gates[job.Id] = new TaskCompletionSource();
        var runs = jobs.Select(job => { rig.Runner.Enqueue(job.Id); return rig.Runner.RunQueuedJobAsync(job.Id, CancellationToken.None); }).ToList();

        Assert.True(await RunnerTestExtensions.Eventually(() => { lock (startOrder) return running == 2; }));
        await Task.Delay(500);
        lock (startOrder) Assert.Equal(2, running);                                    // the other two wait, still queued
        Assert.Equal(new[] { jobs[0].Id, jobs[1].Id }, startOrder.ToArray());          // in the order they were queued
        Assert.Equal(JobStatus.Queued, rig.Store.Read(jobs[2].Id, r => r.Status));

        rig.Settings.Save(new AppSettings { MaxParallelJobs = 3, AccountsDocumentPath = Path.Combine(rig.Env.Root, "a.docx") });
        Assert.True(await RunnerTestExtensions.Eventually(() => { lock (startOrder) return running == 3; }));   // the third started, nobody left

        gates[jobs[0].Id].SetResult();
        Assert.True(await RunnerTestExtensions.Eventually(() => { lock (startOrder) return startOrder.Count == 4; }));
        foreach (var gate in gates.Values) gate.TrySetResult();
        Assert.True(await Task.WhenAll(runs).Completes());

        Assert.Equal(3, peak);
        Assert.All(jobs, job => Assert.Equal(JobStatus.Applied, rig.Store.Read(job.Id, r => r.Status)));
    }
}
