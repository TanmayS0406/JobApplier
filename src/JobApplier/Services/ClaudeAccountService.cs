using JobApplier.Claude;

namespace JobApplier.Services;

/// <summary>
/// Sign-in state of the Claude Code CLI. JobApplier never sees Claude credentials: "Sign in" opens the CLI's own OAuth flow
/// (<c>claude auth login</c>) in a console window and the browser, and the CLI keeps the tokens.
/// </summary>
public sealed class ClaudeAccountService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeAuthStatus? _cached;
    private DateTime _cachedAtUtc;

    public ClaudeAccountService(SettingsService settings) => _settings = settings;

    public string? CliPath => ClaudeCli.Locate(_settings.Current.ClaudePath);

    /// <summary>Status from <c>claude auth status</c>, cached briefly because the UI polls it.</summary>
    public async Task<ClaudeAuthStatus> GetStatusAsync(CancellationToken cancellationToken, bool force = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && _cached != null && DateTime.UtcNow - _cachedAtUtc < CacheFor) return _cached;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                _cached = await ClaudeCli.GetAuthStatusAsync(CliPath, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _cached = new ClaudeAuthStatus(CliPath != null, false, "Timed out checking the sign-in.");
            }
            _cachedAtUtc = DateTime.UtcNow;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens the OAuth sign-in. Returns an error message, or null when the sign-in window was opened.</summary>
    public string? StartSignIn()
    {
        var cli = CliPath;
        if (cli == null) return "Claude Code CLI not found. Install it from https://claude.com/claude-code or set its path below.";
        try
        {
            ClaudeCli.StartLogin(cli);
            _cached = null;
            return null;
        }
        catch (Exception ex)
        {
            return "Could not open the sign-in window: " + ex.Message;
        }
    }

    public async Task<string?> SignOutAsync(CancellationToken cancellationToken)
    {
        var cli = CliPath;
        if (cli == null) return "Claude Code CLI not found.";
        await ClaudeCli.LogoutAsync(cli, cancellationToken);
        _cached = null;
        return null;
    }
}
