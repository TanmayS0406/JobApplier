using System.Diagnostics;

namespace JobApplier.Claude;

/// <summary>
/// Thin wrapper around Google's Gemini CLI. Like the Claude side, JobApplier never handles the credentials itself: signing in
/// is the CLI's own "Login with Google" flow (it keeps the tokens in <c>~/.gemini/oauth_creds.json</c>), or an API key the user
/// has put in the <c>GEMINI_API_KEY</c> environment variable.
/// </summary>
public static class GeminiCli
{
    /// <summary>Environment variables the CLI reads an API key from, in the order they are checked here.</summary>
    public static readonly string[] ApiKeyVariables = { "GEMINI_API_KEY", "GOOGLE_API_KEY" };

    /// <summary>Resolves the CLI from the configured path, PATH, or the npm global folder. Null when not found.</summary>
    public static string? Locate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        var names = OperatingSystem.IsWindows() ? new[] { "gemini.cmd", "gemini.exe" } : new[] { "gemini" };
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try { candidates.Add(Path.Combine(dir.Trim(), name)); }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }

        if (OperatingSystem.IsWindows())
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "gemini.cmd")); // npm i -g

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Where the CLI caches the Google login. <paramref name="home"/> is for tests; it defaults to the user profile.</summary>
    public static string CredentialsFile(string? home = null) =>
        Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");

    public static bool HasGoogleLogin(string? home = null) => File.Exists(CredentialsFile(home));

    /// <summary>The name of the environment variable that holds an API key, or null when there is none.</summary>
    public static string? ApiKeyVariable(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return ApiKeyVariables.FirstOrDefault(v => !string.IsNullOrWhiteSpace(environment(v)));
    }

    /// <summary>
    /// Opens a console window running <c>gemini</c>. On the first start it asks how to sign in: choose <i>Login with Google</i>, finish
    /// in the browser, then leave with <c>/quit</c>.
    /// </summary>
    public static Process? StartLogin(string cliPath)
    {
        var note = "echo Choose 'Login with Google', finish in the browser, then type /quit to close Gemini.";
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + note + " & echo. & " + ClaudeCli.QuoteArgument(cliPath) + " & echo. & pause\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return Process.Start(info);
    }

    /// <summary>
    /// Signs out by removing the cached Google login, which is all "logout" means for this CLI. Returns false when there was
    /// nothing to remove. An API key in the environment is not touched: remove the variable to stop using it.
    /// </summary>
    public static bool SignOut(string? home = null)
    {
        var file = CredentialsFile(home);
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }
}
