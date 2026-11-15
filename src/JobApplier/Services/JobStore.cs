using System.Collections.Concurrent;
using JobApplier.Domain;
using JobApplier.Infrastructure;

namespace JobApplier.Services;

/// <summary>
/// In-memory source of truth for jobs, flushed to <c>jobs/&lt;id&gt;/job.json</c> about once a second.
/// Jobs are mutated by background workers while the UI reads them, so every access goes through
/// <see cref="Update"/> / <see cref="Read{T}"/>, which lock the individual record.
/// </summary>
public sealed class JobStore : BackgroundService
{
    private const int MaxLogEntries = 600;

    private readonly AppPaths _paths;
    private readonly ILogger<JobStore> _logger;
    private readonly ConcurrentDictionary<Guid, JobRecord> _jobs = new();
    private readonly ConcurrentDictionary<Guid, byte> _dirty = new();

    public JobStore(AppPaths paths, ILogger<JobStore> logger)
    {
        _paths = paths;
        _logger = logger;
        LoadAll();
    }

    private void LoadAll()
    {
        foreach (var folder in Directory.GetDirectories(_paths.JobsRoot))
        {
            var record = JsonFile.Read<JobRecord>(Path.Combine(folder, "job.json"));
            if (record == null) continue;

            if (!record.Status.IsTerminal())
            {
                // The process that was working on it is gone.
                record.Status = JobStatus.Failed;
                record.StatusDetail = "Interrupted: the app was restarted while this job was running. Press Retry to continue.";
                record.PendingHelp = null;
                _dirty[record.Id] = 0;
            }
            if (UpgradeLegacyLimitFailure(record)) _dirty[record.Id] = 0;
            _jobs[record.Id] = record;
        }
    }

    /// <summary>
    /// Jobs that failed on a token / quota limit before such failures were tracked carry only the CLI's own message
    /// ("You've hit your session limit - resets 8pm"), so nothing offered to retry them when the limit was over. Recognise them
    /// from that message and give them the same state a new limit failure gets. The reset time is read relative to when the job
    /// failed, so "resets 8pm" means the 8pm after the failure, which may already be past: the job is then retry-able at once.
    /// </summary>
    internal static bool UpgradeLegacyLimitFailure(JobRecord record)
    {
        if (record.Status != JobStatus.Failed || record.LimitHit || !UsageLimit.IsLimit(record.StatusDetail)) return false;

        var failedAt = record.UpdatedUtc;
        var reset = UsageLimit.ResetTime(record.StatusDetail, failedAt);
        record.LimitHit = true;
        record.LimitResetKnown = reset != null;
        record.RetryAfterUtc = reset ?? failedAt + UsageLimit.Backoff(0);
        return true;
    }

    public void Add(JobRecord record)
    {
        _jobs[record.Id] = record;
        MarkDirty(record.Id);
        Flush();
    }

    public bool Exists(Guid id) => _jobs.ContainsKey(id);

    public bool Update(Guid id, Action<JobRecord> mutate)
    {
        if (!_jobs.TryGetValue(id, out var record)) return false;
        lock (record)
        {
            mutate(record);
            record.UpdatedUtc = DateTime.UtcNow;
        }
        MarkDirty(id);
        return true;
    }

    public T? Read<T>(Guid id, Func<JobRecord, T> select)
    {
        if (!_jobs.TryGetValue(id, out var record)) return default;
        lock (record) return select(record);
    }

    public IReadOnlyList<T> ReadAll<T>(Func<JobRecord, T> select)
    {
        var result = new List<(DateTime Created, T Value)>();
        foreach (var record in _jobs.Values)
        {
            lock (record) result.Add((record.CreatedUtc, select(record)));
        }
        return result.OrderByDescending(r => r.Created).Select(r => r.Value).ToList();
    }

    public void SetStatus(Guid id, JobStatus status, string detail = "")
    {
        Update(id, r =>
        {
            r.Status = status;
            r.StatusDetail = SecretRedactor.Redact(detail, r.Password);
        });
    }

    /// <summary>Appends a log line. The job's generated password is masked so it can never reach the UI or disk.</summary>
    public void Log(Guid id, string kind, string text)
    {
        Update(id, r =>
        {
            r.Log.Add(new LogEntry { Kind = kind, Text = SecretRedactor.Redact(text, r.Password) });
            if (r.Log.Count > MaxLogEntries) r.Log.RemoveRange(0, r.Log.Count - MaxLogEntries);
        });
    }

    public bool Remove(Guid id)
    {
        if (!_jobs.TryRemove(id, out _)) return false;
        _dirty.TryRemove(id, out _);
        try
        {
            var folder = _paths.JobFolder(id);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not fully delete job folder for {JobId}", id);
        }
        return true;
    }

    /// <summary>
    /// Removes every job the predicate accepts, with its folder, and returns how many were removed. The predicate runs under
    /// each job's lock, so it sees a consistent record.
    /// </summary>
    public int RemoveWhere(Func<JobRecord, bool> predicate)
    {
        var ids = _jobs.Values.Where(r => { lock (r) return predicate(r); }).Select(r => r.Id).ToList();
        return ids.Count(Remove);
    }

    private void MarkDirty(Guid id) => _dirty[id] = 0;

    /// <summary>Writes every changed job to disk.</summary>
    public void Flush()
    {
        foreach (var id in _dirty.Keys.ToList())
        {
            if (!_dirty.TryRemove(id, out _)) continue;
            if (!_jobs.TryGetValue(id, out var record)) continue;
            try
            {
                lock (record) JsonFile.Write(_paths.JobFile(id), record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dirty[id] = 0; // try again on the next tick
                _logger.LogWarning(ex, "Could not save job {JobId}", id);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) Flush();
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Flush();
    }
}
