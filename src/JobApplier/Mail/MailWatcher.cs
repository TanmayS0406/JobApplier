namespace JobApplier.Mail;

/// <summary>Checks the mailbox for replies now and then while the app is running, as long as Gmail is connected.</summary>
public sealed class MailWatcher : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly MailChecker _checker;
    private readonly IMailAccess _access;
    private readonly ILogger<MailWatcher> _logger;

    public MailWatcher(MailChecker checker, IMailAccess access, ILogger<MailWatcher> logger)
    {
        _checker = checker;
        _access = access;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await CheckOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // the app is shutting down
        }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_access.Connected) await _checker.CheckAsync(rescan: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever goes wrong, the watcher must keep going: the next pass may work.
            _logger.LogWarning(ex, "Mail check failed.");
        }
    }
}
