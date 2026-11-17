using System.Collections.Concurrent;
using System.Threading.Channels;
using JobApplier.Agents;
using JobApplier.Claude;
using JobApplier.Domain;
using JobApplier.Infrastructure;
using Microsoft.Agents.AI.Workflows;

namespace JobApplier.Services;

/// <summary>
/// Background engine. Jobs are queued and up to <see cref="AppSettings.MaxParallelJobs"/> run at the same time, each as its own
/// Agent Framework workflow with its own Claude process and its own visible browser window.
/// </summary>
public sealed class JobRunner : BackgroundService
{
    private readonly JobStore _store;
    private readonly HumanHelpBroker _help;
    private readonly SettingsService _settings;
    private readonly PipelineServices _pipeline;
    private readonly ILogger<JobRunner> _logger;

    /// <summary>Automatic retries per job for one usage limit; after that the job is left for the user (a long outage must not loop forever).</summary>
    public const int MaxAutoRetries = 8;

    /// <summary>A little after the stated reset time, so the first request does not land on the last second of the limit.</summary>
    private static readonly TimeSpan ResetMargin = TimeSpan.FromSeconds(60);

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private int _running;
    private long _pausedUntilTicks;

    public JobRunner(JobStore store, HumanHelpBroker help, SettingsService settings, PipelineServices pipeline, ILogger<JobRunner> logger)
    {
        _store = store;
        _help = help;
        _settings = settings;
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <summary>Queues a job. Returns false if it is already queued or running.</summary>
    public bool Enqueue(Guid jobId)
    {
        if (!_store.Exists(jobId)) return false;

        var cts = new CancellationTokenSource();
        if (!_active.TryAdd(jobId, cts))
        {
            cts.Dispose();
            return false;
        }

        _store.Update(jobId, r =>
        {
            r.Status = JobStatus.Queued;
            r.StatusDetail = "Waiting for a free slot";
            r.PendingHelp = null;
        });
        _queue.Writer.TryWrite(jobId);
        return true;
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
            }
            if (applyAnyway) r.Options.MinMatchPercent = 0;   // "apply anyway" is explicit and wins over a minimum

            r.LimitHit = false;
            r.RetryAfterUtc = null;
            if (!automatic)
            {
                r.AutoRetry = false;
                r.LimitRetries = 0;
            }
        });
        _store.Log(jobId, "info", automatic ? "Retrying automatically: the usage limit should have reset."
            : applyAnyway ? "Retrying, ignoring the minimum match."
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
            return ticks > DateTime.UtcNow.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
    }

    /// <summary>Holds back new jobs until the limit should be over (plus a small margin). A later, longer limit wins; a shorter one never shortens it.</summary>
    public void NoteUsageLimit(DateTime? resetUtc)
    {
        var now = DateTime.UtcNow;
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
    /// Re-queues the armed jobs whose limit should be over. Called every few seconds; also after a restart, because the flag lives in
    /// <c>job.json</c>. Nothing is retried while new jobs are being held back, and a job that keeps hitting the limit is given up on
    /// after <see cref="MaxAutoRetries"/> attempts. Returns how many were re-queued.
    /// </summary>
    internal int RetryDueJobs(DateTime nowUtc)
    {
        if (PausedUntilUtc is { } until && until > nowUtc) return 0;

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

    public void Cancel(Guid jobId)
    {
        if (_active.TryGetValue(jobId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* finished just now */ }
        }
        _help.CancelJob(jobId);
    }

    public bool IsActive(Guid jobId) => _active.ContainsKey(jobId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = Task.Run(() => AutoRetryLoopAsync(stoppingToken), CancellationToken.None);

        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                // The AI is out of tokens: leave the job in the queue until the limit should be over.
                while (PausedUntilUtc != null)
                    await Task.Delay(1000, stoppingToken);

                // The limit is read every time, so changing it in Settings applies to the next job without a restart.
                while (Volatile.Read(ref _running) >= _settings.Current.MaxParallelJobs)
                    await Task.Delay(250, stoppingToken);

                if (!_queue.Reader.TryRead(out var jobId)) continue;
                if (!_active.TryGetValue(jobId, out var cts)) continue;

                Interlocked.Increment(ref _running);
                _ = Task.Run(() => RunJobAsync(jobId, cts, stoppingToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task AutoRetryLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    RetryDueJobs(DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Automatic retry check failed"); // never let one bad pass end the loop
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task RunJobAsync(Guid jobId, CancellationTokenSource cts, CancellationToken appStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, appStopping);
        try
        {
            if (linked.IsCancellationRequested)
            {
                _store.SetStatus(jobId, JobStatus.Cancelled, "Cancelled before it started.");
                return;
            }

            _store.Log(jobId, "info", "Started.");
            await JobWorkflow.RunAsync(JobWorkflow.Build(_pipeline), jobId, linked.Token);
        }
        catch (OperationCanceledException)
        {
            _store.SetStatus(jobId, JobStatus.Cancelled, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} crashed", jobId);
            _store.SetStatus(jobId, JobStatus.Failed, "Unexpected error: " + ex.Message);
            _store.Log(jobId, "error", "Unexpected error: " + ex.Message);
        }
        finally
        {
            _help.CancelJob(jobId);

            // Whatever happened, a job must not stay "running" once its workflow is gone.
            if (!_store.Read(jobId, r => r.Status).IsTerminal())
                _store.SetStatus(jobId, JobStatus.Failed, "Stopped unexpectedly. Press Retry.");

            try { File.Delete(Path.Combine(_pipeline.Paths.PrivateFolder(jobId), "secrets.env")); } catch (IOException) { /* gone with the job */ }

            _active.TryRemove(jobId, out _);
            cts.Dispose();
            Interlocked.Decrement(ref _running);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var cts in _active.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        }
        ClaudeCli.KillAll(); // Claude -> Playwright MCP -> browser windows
        await base.StopAsync(cancellationToken);
    }
}
