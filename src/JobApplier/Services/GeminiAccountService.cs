using JobApplier.Claude;

namespace JobApplier.Services;

/// <summary>Sign-in state of the Gemini CLI, in the same shape as the Claude one so the UI can treat both alike.</summary>
public sealed record GeminiAuthStatus(bool CliFound, bool LoggedIn, string Detail);

/// <summary>
/// Sign-in state of the Gemini CLI. JobApplier never sees Google credentials: "Sign in" opens the CLI's own "Login with Google"
/// in a console window and the browser, and the CLI keeps the tokens. An API key in <c>GEMINI_API_KEY</c> also counts as signed in.
/// <para>
/// The CLI has no "who am I" command, so the state is read from what it leaves behind (the cached login file or the key variable).
/// That tells whether a login exists, not whether it is still valid; an expired one shows up as a clear error on the first job.
/// </para>
/// </summary>
public sealed class GeminiAccountService
{
    private readonly SettingsService _settings;
    private readonly string? _home;
    private readonly Func<string, string?>? _environment;

    /// <param name="home">User profile folder to look in; null = the real one (the parameter is for tests).</param>
    /// <param name="environment">Environment lookup; null = the real one (for tests).</param>
    public GeminiAccountService(SettingsService settings, string? home = null, Func<string, string?>? environment = null)
    {
        _settings = settings;
        _home = home;
        _environment = environment;
    }

    public string? CliPath => GeminiCli.Locate(_settings.Current.GeminiPath);

    public GeminiAuthStatus GetStatus()
    {
        var found = CliPath != null;
        var key = GeminiCli.ApiKeyVariable(_environment);
        if (key != null) return new GeminiAuthStatus(found, true, "API key (" + key + ")");
        if (GeminiCli.HasGoogleLogin(_home)) return new GeminiAuthStatus(found, true, "Google account");
        return new GeminiAuthStatus(found, false, "Not signed in.");
    }

    /// <summary>Opens the "Login with Google" console. Returns an error message, or null when the window was opened.</summary>
    public string? StartSignIn()
    {
        var cli = CliPath;
        if (cli == null) return "Gemini CLI not found. Install it with 'npm install -g @google/gemini-cli' or set its path below.";
        try
        {
            GeminiCli.StartLogin(cli);
            return null;
        }
        catch (Exception ex)
        {
            return "Could not open the sign-in window: " + ex.Message;
        }
    }

    /// <summary>Removes the cached Google login. Returns an error message, or null (also when there was nothing to remove).</summary>
    public string? SignOut()
    {
        try
        {
            GeminiCli.SignOut(_home);
            return GeminiCli.ApiKeyVariable(_environment) is { } key
                ? $"The Google login was removed, but {key} is still set in the environment, so Gemini will keep working with it."
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Could not remove the login: " + ex.Message;
        }
    }
}
