namespace JobApplier.Services;

/// <summary>
/// At start-up: puts the jobs that were unfinished when the app was closed back in the Hangfire queue, and makes sure the recurring check for
/// ended usage limits is registered. The database has already been migrated by then (see <c>DatabaseStartup.Migrate</c>).
/// </summary>
public sealed class JobRecoveryService : IHostedService
{
    private readonly JobRunner _runner;
    private readonly IJobQueue _queue;
    private readonly ILogger<JobRecoveryService> _logger;

    public JobRecoveryService(JobRunner runner, IJobQueue queue, ILogger<JobRecoveryService> logger)
    {
        _runner = runner;
        _queue = queue;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _queue.KeepCheckingForRetries();
            _runner.RecoverAfterRestart();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The app still works; the user can retry what did not come back by hand.
            _logger.LogError(ex, "Could not put the unfinished jobs back in the queue.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
