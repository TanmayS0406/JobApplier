using JobApplier.Claude;

namespace JobApplier.Services;

/// <summary>
/// Sign-in state of the Gemini CLI, in the same shape as the Claude one so the UI can treat both alike.
/// <paramref name="Source"/> says where the sign-in comes from: <c>api-key-env</c>, <c>api-key-file</c>, <c>google</c>, or empty.
/// <paramref name="NpmFound"/> is whether the CLI could be installed from here (Node.js is present).
/// </summary>
public sealed record GeminiAuthStatus(bool CliFound, bool LoggedIn, string Detail, string Source = "", bool NpmFound = false);

/// <summary>
/// Sign-in for the Gemini CLI, made as short as it can be:
/// <list type="bullet">
/// <item><b>Install</b> the CLI with one click when it is missing.</item>
/// <item><b>Paste an API key</b>: it is checked with Google by the caller, then saved where the CLI reads it.</item>
/// <item><b>Sign in with Google</b>: opens the CLI's own login, and closes that window by itself when the login has landed.</item>
/// </list>
/// JobApplier never sees Google credentials. The CLI has no "who am I" command, so the state is read from what it leaves behind
/// (a key, or the cached login file). That tells whether a sign-in exists, not whether it is still valid; an expired one shows up
/// as a clear error on the first job.
/// </summary>
public sealed class GeminiAccountService
{
    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(3);

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
        var npm = SystemChecks.FindOnPath(OperatingSystem.IsWindows() ? new[] { "npm.cmd", "npm.exe" } : new[] { "npm" }) != null;

        if (GeminiCli.ApiKeyVariable(_environment) is { } variable)
            return new GeminiAuthStatus(found, true, "API key (" + variable + ")", "api-key-env", npm);
        if (GeminiCli.ReadStoredApiKey(_home) is { } stored)
            return new GeminiAuthStatus(found, true, "API key (" + GeminiCli.Mask(stored.Value) + ")", "api-key-file", npm);
        if (GeminiCli.HasGoogleLogin(_home))
            return new GeminiAuthStatus(found, true, "Google account", "google", npm);

        return new GeminiAuthStatus(found, false, "Not signed in.", "", npm);
    }

    /// <summary>Opens a console that installs the Gemini CLI. Returns an error message, or null when the window was opened.</summary>
    public string? StartInstall()
    {
        if (CliPath != null) return "The Gemini CLI is already installed.";
        try
        {
            GeminiCli.StartInstall();
            return null;
        }
        catch (FileNotFoundException)
        {
            return "Node.js (npm) is needed to install the Gemini CLI. Install Node.js 18 or newer from https://nodejs.org first, then press this again.";
        }
        catch (Exception ex)
        {
            return "Could not open the install window: " + ex.Message + " You can run it yourself: " + GeminiCli.InstallCommand;
        }
    }

    /// <summary>Opens the "Login with Google" console and closes it once the login lands. Returns an error message, or null.</summary>
    public string? StartSignIn()
    {
        var cli = CliPath;
        if (cli == null) return "Gemini CLI not found. Install it with the button above, or set its path below.";
        try
        {
            var since = DateTime.UtcNow.AddSeconds(-1);
            var window = GeminiCli.StartLogin(cli);
            if (window != null) _ = Task.Run(() => CloseWhenSignedInAsync(window, since));
            return null;
        }
        catch (Exception ex)
        {
            return "Could not open the sign-in window: " + ex.Message;
        }
    }

    /// <summary>
    /// Saves an API key where the CLI reads it. Returns an error message, or null. The key is checked for shape only here; asking
    /// Google whether it works is the caller's job (<see cref="GeminiKeyChecker"/>), so this stays free of network calls.
    /// </summary>
    public string? SaveApiKey(string? key)
    {
        key = GeminiCli.NormalizeKey(key);
        if (KeyShapeError(key) is { } shapeError) return shapeError;
        try
        {
            GeminiCli.StoreApiKey(key, _home);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Could not save the key: " + ex.Message;
        }
    }

    /// <summary>What is wrong with the look of a pasted key, or null when it could be one.</summary>
    public static string? KeyShapeError(string? key)
    {
        if (GeminiCli.IsPlausibleKey(key)) return null;

        // Says what is wrong without repeating any of the text: it may be a key.
        key ??= "";
        const string start = "That does not look like a Gemini API key";
        if (key.Length == 0) return start + ": nothing was pasted.";
        if (key.Any(char.IsWhiteSpace)) return start + ": it contains spaces or line breaks. A key is one string; copy only the key.";
        if (key.Length < 20)
            return start + $": it is only {key.Length} characters. AI Studio's key list shows just the end of each key (like ...x9Zq); use the copy button on the key's row to copy the whole key.";
        if (key.Length > 128) return start + ": it is far longer than a key. Copy only the key.";
        return start + ": it contains characters a key does not have (such as = : / or quotes). Copy only the key itself.";
    }

    /// <summary>Removes the key this app saved. Returns an error message, or null (also when there was none).</summary>
    public string? RemoveApiKey()
    {
        try
        {
            GeminiCli.RemoveApiKey(_home);
            return GeminiCli.ApiKeyVariable(_environment) is { } variable
                ? $"The saved key was removed, but {variable} is still set in your environment, so Gemini keeps using it."
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Could not remove the key: " + ex.Message;
        }
    }

    /// <summary>Removes the cached Google login. Returns a message to show, or null when that is all there was to say.</summary>
    public string? SignOut()
    {
        try
        {
            GeminiCli.SignOut(_home);
            return GeminiCli.ApiKeyVariable(_environment) is { } variable
                ? $"The Google login was removed, but {variable} is still set in the environment, so Gemini will keep working with it."
                : GeminiCli.ReadStoredApiKey(_home) != null
                    ? "The Google login was removed. A saved API key is still in use; remove it too if you want to be fully signed out."
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Could not remove the login: " + ex.Message;
        }
    }

    private async Task CloseWhenSignedInAsync(System.Diagnostics.Process window, DateTime since)
    {
        try
        {
            var signedIn = await GeminiCli.WaitForNewLoginAsync(since, LoginWindow, TimeSpan.FromSeconds(1), _home, stop: () => window.HasExited);
            if (!signedIn) return;

            await Task.Delay(CloseDelay);   // let the CLI finish what it is writing before its window goes
            ClaudeCli.KillTree(window);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // the window is already gone: nothing to close
        }
    }
}
