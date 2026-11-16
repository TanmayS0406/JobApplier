using JobApplier.Services;

namespace JobApplier.Mail;

/// <summary>
/// Checks the mailbox for replies now and then while the app runs - but only once the user has turned reply tracking on, only as often
/// as the settings say (each check uses some of the user's Claude usage), and never while the Claude usage limit is holding jobs back.
/// </summary>
public sealed class MailWatcher : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    private readonly MailChecker _checker;
    private readonly ReplyStore _store;
    private readonly SettingsService _settings;
    private readonly JobRunner _runner;
    private readonly ILogger<MailWatcher> _logger;

    public MailWatcher(MailChecker checker, ReplyStore store, SettingsService settings, JobRunner runner, ILogger<MailWatcher> logger)
    {
        _checker = checker;
        _store = store;
        _settings = settings;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            using var timer = new PeriodicTimer(Tick);
            do
            {
                await CheckIfDueAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // the app is shutting down
        }
    }

    /// <summary>True when tracking is on, automatic checks are not switched off, the last check is old enough and Claude is not out of usage.</summary>
    internal static bool IsDue(MailState state, int everyHours, DateTime nowUtc, DateTime? pausedUntilUtc) =>
        state.Enabled
        && everyHours > 0
        && !(pausedUntilUtc is { } until && until > nowUtc)
        && (state.LastCheckedUtc is not { } last || nowUtc - last >= TimeSpan.FromHours(everyHours));

    private async Task CheckIfDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsDue(_store.State(), _settings.Current.MailAutoCheckHours, DateTime.UtcNow, _runner.PausedUntilUtc)) return;
            await _checker.CheckAsync(rescan: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever goes wrong, the watcher must keep going: the next pass may work.
            _logger.LogWarning(ex, "Mail check failed.");
        }
    }
}
