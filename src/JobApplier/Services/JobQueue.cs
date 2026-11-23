using Hangfire;

namespace JobApplier.Services;

/// <summary>Names of the Hangfire queues. Jobs are listed after the default queue, so the small housekeeping tasks are never stuck behind a long job.</summary>
public static class JobQueues
{
    public const string Jobs = "jobs";
    public const string Default = "default";

    /// <summary>The order the server takes work in: earlier queues first.</summary>
    public static readonly string[] All = { Default, Jobs };
}

/// <summary>
/// The durable queue the runner hands jobs to. It is Hangfire in the app (the queue lives in the SQL Server database, so it survives the
/// app being closed); the tests use a stand-in that only records what was queued.
/// </summary>
public interface IJobQueue
{
    /// <summary>Queues a run of the job to start as soon as a worker is free. Returns the queue entry's id.</summary>
    string Enqueue(Guid jobId);

    /// <summary>Queues a run of the job for a later time (UTC). Returns the queue entry's id.</summary>
    string Schedule(Guid jobId, DateTime runAtUtc);

    /// <summary>Takes an entry out of the queue. Best effort: an entry that already ran or is gone is not an error.</summary>
    void Delete(string queueId);

    /// <summary>Makes sure the queue keeps looking for jobs whose usage limit should be over (a recurring Hangfire job, kept in the database).</summary>
    void KeepCheckingForRetries();
}

public sealed class HangfireJobQueue : IJobQueue
{
    public const string RetryCheckId = "usage-limit-retries";

    private readonly IBackgroundJobClient _client;
    private readonly IRecurringJobManager _recurring;

    public HangfireJobQueue(IBackgroundJobClient client, IRecurringJobManager recurring)
    {
        _client = client;
        _recurring = recurring;
    }

    // The CancellationToken argument is replaced by Hangfire with a token that is cancelled when the server shuts down (or the job is deleted).
    public string Enqueue(Guid jobId) =>
        _client.Enqueue<JobRunner>(runner => runner.RunQueuedJobAsync(jobId, CancellationToken.None));

    public string Schedule(Guid jobId, DateTime runAtUtc) =>
        _client.Schedule<JobRunner>(runner => runner.RunQueuedJobAsync(jobId, CancellationToken.None),
            new DateTimeOffset(DateTime.SpecifyKind(runAtUtc, DateTimeKind.Utc)));

    public void Delete(string queueId)
    {
        try { _client.Delete(queueId); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* an entry that is already gone needs no deleting */ }
    }

    public void KeepCheckingForRetries() =>
        _recurring.AddOrUpdate<JobRunner>(RetryCheckId, runner => runner.CheckDueRetries(), Cron.Minutely());
}
