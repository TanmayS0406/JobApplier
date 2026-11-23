using System.Collections.Concurrent;
using JobApplier.Persistence;
using JobApplier.Domain;
using JobApplier.Infrastructure;

namespace JobApplier.Services;

/// <summary>
/// In-memory source of truth for jobs, written to the database (<see cref="IJobPersistence"/>) about once a second.
/// Jobs are mutated by background workers while the UI reads them, so every access goes through
/// <see cref="Update"/> / <see cref="Read{T}"/>, which lock the individual record.
/// The files of a job (its screenshot, the agent's workspace) stay in its folder under the data folder.
/// </summary>
public sealed class JobStore : BackgroundService
{
    private const int MaxLogEntries = 600;

    private readonly AppPaths _paths;
    private readonly IJobPersistence _persistence;
    private readonly ILogger<JobStore> _logger;
    private readonly ConcurrentDictionary<Guid, JobRecord> _jobs = new();
    private readonly ConcurrentDictionary<Guid, byte> _dirty = new();

    public JobStore(AppPaths paths, IJobPersistence persistence, ILogger<JobStore> logger)
    {
        _paths = paths;
        _persistence = persistence;
        _logger = logger;
        LoadAll();
    }

    /// <summary>
    /// Loads what the database holds. A job that was still running when the app stopped is left as it was: <see cref="JobRunner.RecoverAfterRestart"/>
    /// puts it back in the queue, and it continues from the stage it had reached.
    /// </summary>
    private void LoadAll()
    {
        foreach (var record in _persistence.LoadAll())
        {
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
            _persistence.Delete(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not delete job {JobId} from the database", id);
        }
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

    /// <summary>Writes every changed job to the database.</summary>
    public void Flush()
    {
        foreach (var id in _dirty.Keys.ToList())
        {
            if (!_dirty.TryRemove(id, out _)) continue;
            if (!_jobs.TryGetValue(id, out var record)) continue;
            try
            {
                lock (record) _persistence.Save(record);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database is busy or away for a moment: the change stays in memory and is written on the next tick.
                _dirty[id] = 0;
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
