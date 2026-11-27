using System.Collections.Concurrent;
using Hangfire;
using JobApplier.Agents;
using JobApplier.Domain;
using JobApplier.Infrastructure;

namespace JobApplier.Services;

/// <summary>
/// Runs the jobs. Every job is a Hangfire background job (<see cref="RunQueuedJobAsync"/>), so the queue lives in the SQL Server database:
/// closing the app does not lose it, and <see cref="RecoverAfterRestart"/> puts what was unfinished back in line, to continue from the
/// stage it had reached (each finished stage is stored on the job). Up to <see cref="AppSettings.MaxParallelJobs"/> run at the same time,
/// each as its own Agent Framework workflow with its own Claude process and its own visible browser window.
/// When the AI runs out of tokens, the job that hit the limit waits for the reset and continues by itself (see <see cref="ArmAutoRetry"/>).
/// </summary>
public sealed class JobRunner
{
    private readonly JobStore _store;
    private readonly HumanHelpBroker _help;
    private readonly SettingsService _settings;
    private readonly PipelineServices _pipeline;
    private readonly IJobQueue _queue;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ILogger<JobRunner> _logger;

    /// <summary>
    /// Automatic retries per job for one usage limit; after that the job is left for the user. Generous on purpose: a limit whose reset time is not
    /// known is probed again every hour once the pauses have grown, which reaches the longest real limit (a week) well inside this number.
    /// </summary>
    public const int MaxAutoRetries = 200;

    /// <summary>A little after the stated reset time, so the first request does not land on the last second of the limit.</summary>
    private static readonly TimeSpan ResetMargin = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<Guid, PendingJob> _active = new();
    private readonly SlotGate _slots = new();
    private long _pausedUntilTicks;

    /// <summary>A usage limit was hit and nobody has resumed since. Only matters while <see cref="AppSettings.PauseAfterLimit"/> is on.</summary>
    private volatile bool _limitHit;

    /// <summary>How often a job that is held for the user looks again whether it may start.</summary>
    private static readonly TimeSpan HeldRecheck = TimeSpan.FromMinutes(2);

    public JobRunner(JobStore store, HumanHelpBroker help, SettingsService settings, PipelineServices pipeline, IJobQueue queue,
        ILogger<JobRunner> logger, IHostApplicationLifetime? lifetime = null)
    {
        _store = store;
        _help = help;
        _settings = settings;
        _pipeline = pipeline;
        _queue = queue;
        _logger = logger;
        _lifetime = lifetime;
        RunWorkflow = (jobId, cancellationToken) => JobWorkflow.RunAsync(JobWorkflow.Build(_pipeline), jobId, cancellationToken);
    }

    /// <summary>What a worker runs for a job: the whole pipeline. The tests replace it, so the runner can be exercised without an AI agent.</summary>
    internal Func<Guid, CancellationToken, Task> RunWorkflow { get; set; }

    /// <summary>The current time (UTC). The tests move it forward to "wait" for a usage limit to end.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// A job this process owes a run. It is waiting in the queue, running, or put back in the queue for later (usage limit). Cancelling it
    /// while it still waits takes it out of the queue; once a worker has it, cancelling stops the run.
    /// </summary>
    private sealed class PendingJob : IDisposable
    {
        private const int InQueue = 0, WithWorker = 1, Revoked = 2;
        private int _state;

        public CancellationTokenSource Cts { get; } = new();

        /// <summary>A worker takes the job. False when it was cancelled in the meantime, or another worker already has it.</summary>
        public bool TryPickUp() => Interlocked.CompareExchange(ref _state, WithWorker, InQueue) == InQueue;

        /// <summary>The job is cancelled before any worker took it. False when a worker has it (it then stops the run itself).</summary>
        public bool TryRevoke() => Interlocked.CompareExchange(ref _state, Revoked, InQueue) == InQueue;

        /// <summary>The worker hands the job back: it is queued again for later.</summary>
        public void PutBack() => Volatile.Write(ref _state, InQueue);

        public void Dispose() => Cts.Dispose();
    }

    /// <summary>
    /// Queues a job. Returns false if it is already queued or running (or gone). The job is marked queued before the queue is told, so a
    /// worker that picks it up at once cannot be overwritten by that mark.
    /// </summary>
    public bool Enqueue(Guid jobId)
    {
        if (!_store.Exists(jobId)) return false;

        var pending = new PendingJob();
        if (!_active.TryAdd(jobId, pending))
        {
            pending.Dispose();
            return false;
        }

        _store.Update(jobId, r =>
        {
            r.Status = JobStatus.Queued;
            r.StatusDetail = "Waiting for a free slot";
            r.PendingHelp = null;
            r.QueueId = null;
        });

        try
        {
            var queueId = _queue.Enqueue(jobId);
            _store.Update(jobId, r => r.QueueId ??= queueId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not queue job {JobId}", jobId);
            if (_active.TryRemove(jobId, out var removed)) removed.Dispose();
            _store.SetStatus(jobId, JobStatus.Failed, "Could not be put in the queue: " + ex.Message + " Press Retry.");
            _store.Log(jobId, "error", "Could not be put in the queue: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Re-queues a finished job. Work that already succeeded is kept; the stage that did not is redone. A retry the user asks for
    /// cancels any "retry when available" that was armed; one the runner makes by itself (<paramref name="automatic"/>) keeps it,
    /// so the job is picked up again if the limit is still in force.
    /// </summary>
    public bool Retry(Guid jobId, bool applyAnyway = false, bool automatic = false, RetryOptions? options = null)
    {
        var status = _store.Read(jobId, r => r.Status);
        if (!status.IsTerminal() || status == JobStatus.Applied) return false; // never submit a second application

        _store.Update(jobId, r =>
        {
            if (r.Status == JobStatus.NotFound)
            {
                r.Found = null;   // search again ...
                r.Match = null;   // ... and score again from the real description
            }
            r.Apply = null;
            if (options != null)
            {
                r.Options.AutoSubmit = options.AutoSubmit;
                r.Options.MinMatchPercent = options.MinMatchPercent;
                r.Options.RequireVisaOrRelocation = options.RequireVisaOrRelocation;
            }
            if (applyAnyway)
            {
                // "apply anyway" is explicit and wins over a minimum and over the visa / relocation requirement
                r.Options.MinMatchPercent = 0;
                r.Options.RequireVisaOrRelocation = false;
            }

            r.LimitHit = false;
            r.RetryAfterUtc = null;
            if (!automatic)
            {
                r.AutoRetry = false;
                r.LimitRetries = 0;
            }
        });
        _store.Log(jobId, "info", automatic ? "Retrying automatically: the usage limit should have reset."
            : applyAnyway ? "Retrying, ignoring the minimum match and the visa / relocation requirement."
            : options != null ? "Retrying: " + options.Describe() + "." : "Retrying.");
        return Enqueue(jobId);
    }

    /// <summary>
    /// Retries every failed job now, oldest first so the queue keeps the order they were added in. Applied jobs are never touched,
    /// and neither is a job that is already running. Returns how many were queued. Jobs waiting on a usage limit are included:
    /// asking to retry everything overrides their wait (a limit that is still in force simply fails them again and holds the queue).
    /// </summary>
    public int RetryAllFailed(RetryOptions? options = null)
    {
        var ids = _store.ReadAll(r => (r.Id, r.Status)).Where(j => j.Status == JobStatus.Failed).Select(j => j.Id).Reverse().ToList();
        return ids.Count(id => !IsActive(id) && Retry(id, options: options));
    }

    // ---- restart ----

    /// <summary>
    /// Called once when the app starts. Every job that was not finished when the app was closed (waiting, or in the middle of a stage) goes back
    /// in the queue, oldest first, and continues from the stage it had reached. Whatever the previous run left in Hangfire for those jobs is
    /// removed first, so each job is queued exactly once however the app ended (closed normally, or killed). The hold on new jobs is restored
    /// too if a usage limit was still in force. Returns how many jobs were put back in the queue.
    /// </summary>
    public int RecoverAfterRestart()
    {
        var stillLimited = _store.ReadAll(r => (r.WaitingForLimitReset, r.RetryAfterUtc))
            .Where(j => j.WaitingForLimitReset && j.RetryAfterUtc > Clock())
            .Select(j => j.RetryAfterUtc)
            .Max();
        if (stillLimited != null) NoteUsageLimit(stillLimited);
        else if (_store.ReadAll(r => r.WaitingForLimitReset).Any(waiting => waiting)) _limitHit = true;   // a limit was hit before the restart and not resumed

        var unfinished = _store.ReadAll(r => (r.Id, r.Status, r.QueueId, r.CreatedUtc))
            .Where(j => !j.Status.IsTerminal())
            .OrderBy(j => j.CreatedUtc)
            .ToList();

        var queued = 0;
        foreach (var job in unfinished)
        {
            if (job.QueueId != null) _queue.Delete(job.QueueId);
            _store.Update(job.Id, r => r.QueueId = null);

            if (job.Status != JobStatus.Queued)
            {
                _store.Log(job.Id, "info", $"JobApplier was closed while this job was at \"{job.Status.Label()}\". It continues from the stage it had reached." +
                    (job.Status == JobStatus.Applying ? " If the application had already gone through, the site will say so and it is recorded as applied." : ""));
            }
            if (Enqueue(job.Id)) queued++;
        }

        if (queued > 0) _logger.LogInformation("Put {Count} unfinished job(s) back in the queue.", queued);
        return queued;
    }

    // ---- usage limits ----

    /// <summary>
    /// While set, no queued job is started: the AI has run out of tokens, and starting a job would only make it fail the same way.
    /// Null when nothing is being held back.
    /// </summary>
    public DateTime? PausedUntilUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _pausedUntilTicks);
            return ticks > Clock().Ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
    }

    /// <summary>
    /// True while the limit has ended (or not) but the user has chosen to pause after a limit (Settings) and has not pressed Resume yet:
    /// no queued job starts and no waiting job is retried. Turning the setting back on lifts it at once.
    /// </summary>
    public bool HeldByUser => _limitHit && _settings.Current.PauseAfterLimit;

    /// <summary>The user says go on: jobs held since the usage limit start again (the limit itself, if still in force, still holds them).</summary>
    public void ResumeAfterLimit() => _limitHit = false;

    /// <summary>Until when a job must not start, or null when it may: the usage limit, or a short while ahead when the user is holding jobs back.</summary>
    private DateTime? HoldUntil() => PausedUntilUtc ?? (HeldByUser ? Clock() + HeldRecheck : null);

    /// <summary>Holds back new jobs until the limit should be over (plus a small margin). A later, longer limit wins; a shorter one never shortens it.</summary>
    public void NoteUsageLimit(DateTime? resetUtc)
    {
        _limitHit = true;
        var now = Clock();
        var until = (resetUtc ?? now + UsageLimit.Backoff(0)) + ResetMargin;
        if (until > now.AddDays(8)) until = now.AddDays(8);

        long current;
        do
        {
            current = Interlocked.Read(ref _pausedUntilTicks);
            if (current >= until.Ticks) return;
        }
        while (Interlocked.CompareExchange(ref _pausedUntilTicks, until.Ticks, current) != current);

        _logger.LogWarning("The AI usage limit was reached; queued jobs wait until {Until:u}.", until);
    }

    /// <summary>
    /// Marks a limit-hit job to be retried automatically once the limit has reset. False when it is not such a job. The choices of the
    /// retry dialog are saved on the job now: the retry itself happens later, by itself, with whatever the job holds then.
    /// A job that hits a usage limit while running is armed by the runner at once, so nobody has to be there when the limit ends.
    /// </summary>
    public bool ArmAutoRetry(Guid jobId, RetryOptions? options = null)
    {
        if (!_store.Read(jobId, r => r.Status == JobStatus.Failed && r.LimitHit && !r.AutoRetry)) return false;

        _store.Update(jobId, r =>
        {
            r.AutoRetry = true;
            r.LimitRetries = 0;
            if (options != null)
            {
                r.Options.AutoSubmit = options.AutoSubmit;
                r.Options.MinMatchPercent = options.MinMatchPercent;
                r.Options.RequireVisaOrRelocation = options.RequireVisaOrRelocation;
            }
        });
        _store.Log(jobId, "info", options != null
            ? "Will retry automatically once the usage limit has reset: " + options.Describe() + "."
            : "Will retry automatically once the usage limit has reset.");
        return true;
    }

    /// <summary>Arms every limit-hit job that is not already armed. Returns how many.</summary>
    public int ArmAllLimited(RetryOptions? options = null)
    {
        var ids = _store.ReadAll(r => (r.Id, Armable: r.Status == JobStatus.Failed && r.LimitHit && !r.AutoRetry)).Where(j => j.Armable).Select(j => j.Id);
        return ids.Count(id => ArmAutoRetry(id, options));
    }

    /// <summary>Stops waiting for the limit: the job stays failed and is left for the user to retry or delete.</summary>
    public bool DisarmAutoRetry(Guid jobId)
    {
        if (!_store.Read(jobId, r => r.AutoRetry)) return false;

        _store.Update(jobId, r => r.AutoRetry = false);
        _store.Log(jobId, "info", "Stopped waiting for the usage limit to reset.");
        return true;
    }

    /// <summary>
    /// Re-queues the armed jobs whose limit should be over. Runs every minute as a recurring Hangfire job (<see cref="CheckDueRetries"/>), and
    /// the armed state lives in the database, so jobs keep being watched across restarts. Nothing is retried while new jobs are being held back,
    /// and a job that keeps hitting the limit is given up on after <see cref="MaxAutoRetries"/> attempts. Returns how many were re-queued.
    /// </summary>
    internal int RetryDueJobs(DateTime nowUtc)
    {
        if (PausedUntilUtc is { } until && until > nowUtc) return 0;
        if (HeldByUser) return 0;   // the user chose to resume by hand

        var waiting = _store.ReadAll(r => (r.Id, Wait: r.WaitingForLimitReset, r.RetryAfterUtc, r.LimitRetries, r.UpdatedUtc)).Where(j => j.Wait);
        var started = 0;
        foreach (var job in waiting)
        {
            if (IsActive(job.Id)) continue;

            var due = (job.RetryAfterUtc ?? job.UpdatedUtc + UsageLimit.Backoff(job.LimitRetries)) + ResetMargin;
            if (nowUtc < due) continue;

            if (job.LimitRetries >= MaxAutoRetries)
            {
                _store.Update(job.Id, r => r.AutoRetry = false);
                _store.Log(job.Id, "warn", $"Gave up retrying automatically after {MaxAutoRetries} attempts. Press Retry when the usage limit is over.");
                continue;
            }

            _store.Update(job.Id, r => r.LimitRetries++);
            if (Retry(job.Id, automatic: true)) started++;
        }
        return started;
    }

    /// <summary>The recurring Hangfire job that keeps listening for the end of a usage limit.</summary>
    [AutomaticRetry(Attempts = 0)]
    [Queue(JobQueues.Default)]
    public void CheckDueRetries()
    {
        try
        {
            RetryDueJobs(Clock());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Automatic retry check failed"); // one bad pass must not end the checks
        }
    }

    // ---- cancel / state ----

    public void Cancel(Guid jobId)
    {
        if (_active.TryGetValue(jobId, out var pending))
        {
            try { pending.Cts.Cancel(); } catch (ObjectDisposedException) { /* finished just now */ }

            // No worker has it yet: take it out of the queue and finish it here. A worker that has it stops the run itself.
            if (pending.TryRevoke())
            {
                if (_store.Read(jobId, r => r.QueueId) is { } queueId) _queue.Delete(queueId);
                _store.SetStatus(jobId, JobStatus.Cancelled, "Cancelled before it started.");
                _store.Log(jobId, "info", "Cancelled.");
                if (_active.TryRemove(new KeyValuePair<Guid, PendingJob>(jobId, pending))) pending.Dispose();
            }
        }
        _help.CancelJob(jobId);
    }

    /// <summary>True while the job is queued or running (including a job put back in the queue until a usage limit is over).</summary>
    public bool IsActive(Guid jobId) => _active.ContainsKey(jobId);

    // ---- the Hangfire job ----

    /// <summary>
    /// What Hangfire runs for a queued job. Not called by anything else. A queue entry that no longer has work (the job was cancelled, deleted, or
    /// already has a worker) does nothing. <paramref name="shutdown"/> is Hangfire's token for the server stopping.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    [Queue(JobQueues.Jobs)]
    public async Task RunQueuedJobAsync(Guid jobId, CancellationToken shutdown)
    {
        if (!_active.TryGetValue(jobId, out var pending) || !pending.TryPickUp()) return;

        var queuedAgain = false;
        try
        {
            queuedAgain = await RunAsync(jobId, pending, shutdown);
        }
        finally
        {
            if (!queuedAgain) Finish(jobId, pending);
        }
    }

    /// <summary>Returns true when the job was put back in the queue for later instead of being run.</summary>
    private async Task<bool> RunAsync(Guid jobId, PendingJob pending, CancellationToken shutdown)
    {
        // Removed, or finished some other way, since it was queued.
        if (!_store.Exists(jobId) || _store.Read(jobId, r => r.Status.IsTerminal())) return false;

        if (HoldUntil() is { } hold && Defer(jobId, pending, hold)) return true;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pending.Cts.Token, shutdown);
        SlotGate.Lease? slot = null;
        var started = false;
        try
        {
            slot = await _slots.EnterAsync(() => _settings.Current.MaxParallelJobs, linked.Token);

            // A job that ran while this one waited for its slot may have used up the tokens.
            if (HoldUntil() is { } laterHold && Defer(jobId, pending, laterHold)) return true;

            started = true;
            _store.Log(jobId, "info", "Started.");
            await RunWorkflow(jobId, linked.Token);
        }
        catch (OperationCanceledException)
        {
            // The user cancelling is final; the app closing is not (see AfterRun).
            if (pending.Cts.IsCancellationRequested || !Stopping(shutdown))
                _store.SetStatus(jobId, JobStatus.Cancelled, started ? "Cancelled." : "Cancelled before it started.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} crashed", jobId);
            _store.SetStatus(jobId, JobStatus.Failed, "Unexpected error: " + ex.Message);
            _store.Log(jobId, "error", "Unexpected error: " + ex.Message);
        }
        finally
        {
            slot?.Dispose();
        }

        AfterRun(jobId, pending, shutdown);
        return false;
    }

    private void AfterRun(Guid jobId, PendingJob pending, CancellationToken shutdown)
    {
        if (!_store.Exists(jobId)) return;

        // The app is closing: whatever stopped this job (the Claude process being killed, the cancellation) was the shutdown, not the job.
        // It is left queued; the next start puts it back in line and it continues from the stage it had reached.
        if (Stopping(shutdown) && !pending.Cts.IsCancellationRequested && _store.Read(jobId, r => CutShortByShutdown(r)))
        {
            _store.Update(jobId, r =>
            {
                r.Status = JobStatus.Queued;
                r.StatusDetail = "Paused because JobApplier was closed. It continues from where it stopped the next time JobApplier starts.";
                r.PendingHelp = null;
            });
            _store.Log(jobId, "info", "JobApplier was closed. This job continues from where it stopped when JobApplier starts again.");
            return;
        }

        // Whatever happened, a job must not stay "running" once its workflow is gone.
        if (!_store.Read(jobId, r => r.Status).IsTerminal())
            _store.SetStatus(jobId, JobStatus.Failed, "Stopped unexpectedly. Press Retry.");

        // Out of tokens: nothing is wrong with the job, so it waits for the reset and goes on by itself.
        ArmAutoRetry(jobId);
    }

    private static bool CutShortByShutdown(JobRecord job) =>
        !job.Status.IsTerminal() || job.Status == JobStatus.Cancelled || (job.Status == JobStatus.Failed && !job.LimitHit);

    private bool Stopping(CancellationToken shutdown) =>
        shutdown.IsCancellationRequested || (_lifetime?.ApplicationStopping.IsCancellationRequested ?? false);

    /// <summary>The usage limit is in force: this job goes back in the queue for when it should be over, and waits there without holding a worker.</summary>
    private bool Defer(Guid jobId, PendingJob pending, DateTime untilUtc)
    {
        pending.PutBack();
        try
        {
            var queueId = _queue.Schedule(jobId, untilUtc);
            _store.Update(jobId, r =>
            {
                r.Status = JobStatus.Queued;
                r.QueueId = queueId;
                r.StatusDetail = PausedUntilUtc == null
                    ? "Paused: the AI usage limit was reached and pausing after a limit is on. Press Resume on the dashboard (or turn it on in Settings)."
                    : "Waiting for the AI usage limit to reset, " + UsageLimit.Describe(untilUtc, Clock()) + ". It starts by itself.";
            });
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Could not be put back: run it. It fails on the limit the same way and then waits as an armed job.
            _logger.LogWarning(ex, "Could not schedule job {JobId} for after the usage limit", jobId);
            pending.TryPickUp();
            return false;
        }
    }

    private void Finish(Guid jobId, PendingJob pending)
    {
        _help.CancelJob(jobId);

        try { File.Delete(Path.Combine(_pipeline.Paths.PrivateFolder(jobId), "secrets.env")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* gone with the job */ }

        if (_active.TryRemove(new KeyValuePair<Guid, PendingJob>(jobId, pending))) pending.Dispose();
    }
}
