using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using JobApplier.Services;

namespace JobApplier.Claude;

/// <summary>
/// Thin wrapper around Google's Gemini CLI. JobApplier signs in to it in one of two ways, both of which leave the credential
/// where the CLI itself keeps it, so the CLI works the same whether or not this app is running:
/// <list type="bullet">
/// <item><b>Login with Google</b>: the CLI's own browser flow, which caches the login in <c>~/.gemini/oauth_creds.json</c>.</item>
/// <item><b>An API key</b>: written to <c>~/.gemini/.env</c> as <c>GEMINI_API_KEY</c>, the file the CLI reads user-level settings
/// from (or a key already set in the environment).</item>
/// </list>
/// </summary>
public static class GeminiCli
{
    /// <summary>The npm package that provides the CLI.</summary>
    public const string PackageName = "@google/gemini-cli";

    /// <summary>The command a person would type to install it.</summary>
    public const string InstallCommand = "npm install -g " + PackageName;

    /// <summary>Environment variables the CLI reads an API key from, in the order they are checked here.</summary>
    public static readonly string[] ApiKeyVariables = { "GEMINI_API_KEY", "GOOGLE_API_KEY" };

    // Gemini keys are "AIza" plus 35 characters; anything shaped like a token is accepted so a new key format does not need a release.
    private static readonly Regex PlausibleKey = new(@"^[A-Za-z0-9_\-]{20,128}$", RegexOptions.CultureInvariant);

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

    // ---- Google login ----

    /// <summary>Where the CLI caches the Google login. <paramref name="home"/> is for tests; it defaults to the user profile.</summary>
    public static string CredentialsFile(string? home = null) => Path.Combine(Home(home), ".gemini", "oauth_creds.json");

    public static bool HasGoogleLogin(string? home = null) => File.Exists(CredentialsFile(home));

    /// <summary>
    /// Opens a console window running <c>gemini</c>. On the first start it asks how to sign in: the first choice, <i>Login with Google</i>,
    /// is already highlighted, so Enter is all that is needed before the browser opens.
    /// </summary>
    public static Process? StartLogin(string cliPath)
    {
        var note = "echo Press Enter on 'Login with Google', then choose your account in the browser. This window closes by itself.";
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + note + " & echo. & " + ClaudeCli.QuoteArgument(cliPath) + " & echo. & pause\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return Process.Start(info);
    }

    /// <summary>
    /// Waits for a login to appear: the cached credentials file written at or after <paramref name="sinceUtc"/> (an older file is a
    /// previous login, not this one). Gives up at the timeout, or as soon as <paramref name="stop"/> says the sign-in window is gone.
    /// </summary>
    public static async Task<bool> WaitForNewLoginAsync(
        DateTime sinceUtc, TimeSpan timeout, TimeSpan poll, string? home = null, Func<bool>? stop = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var file = CredentialsFile(home);
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) >= sinceUtc) return true;
            if (stop?.Invoke() == true) return false;

            try { await Task.Delay(poll, cancellationToken); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    /// <summary>
    /// Signs out by removing the cached Google login, which is all "logout" means for this CLI. Returns false when there was
    /// nothing to remove. An API key is not touched.
    /// </summary>
    public static bool SignOut(string? home = null)
    {
        var file = CredentialsFile(home);
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    // ---- API key ----

    /// <summary>The name of the environment variable that holds an API key, or null when there is none.</summary>
    public static string? ApiKeyVariable(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return ApiKeyVariables.FirstOrDefault(v => !string.IsNullOrWhiteSpace(environment(v)));
    }

    /// <summary>The CLI's user-level environment file, <c>~/.gemini/.env</c>.</summary>
    public static string EnvFile(string? home = null) => Path.Combine(Home(home), ".gemini", ".env");

    /// <summary>True for a single token of letters, digits, '-' and '_' of a sensible length: what a key looks like, nothing else.</summary>
    public static bool IsPlausibleKey(string? key) => key != null && PlausibleKey.IsMatch(key);

    /// <summary>"AIza...x9Zq": enough to recognise a key without revealing it.</summary>
    public static string Mask(string key) => key.Length <= 8 ? new string('*', key.Length) : key[..4] + "..." + key[^4..];

    /// <summary>The API key saved in <c>~/.gemini/.env</c> (<c>GEMINI_API_KEY=...</c>, also with <c>export</c> and quotes), or null.</summary>
    public static (string Variable, string Value)? ReadStoredApiKey(string? home = null)
    {
        var file = EnvFile(home);
        if (!File.Exists(file)) return null;

        foreach (var raw in File.ReadLines(file))
        {
            if (!TryParseAssignment(raw, out var name, out var value)) continue;
            if (ApiKeyVariables.Contains(name) && value.Length > 0) return (name, value);
        }
        return null;
    }

    /// <summary>
    /// Saves the key as <c>GEMINI_API_KEY</c> in <c>~/.gemini/.env</c>, replacing an earlier one and keeping every other line, so a
    /// file the user maintains by hand is not disturbed. The file is replaced in one step, never left half-written.
    /// </summary>
    public static void StoreApiKey(string key, string? home = null)
    {
        var file = EnvFile(home);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (TryParseAssignment(lines[i], out var name, out _) && name == "GEMINI_API_KEY")
            {
                if (!replaced) lines[i] = "GEMINI_API_KEY=" + key;
                else lines[i] = null!;   // a duplicate of the line just written
                replaced = true;
            }
        }
        lines.RemoveAll(l => l == null);
        if (!replaced) lines.Add("GEMINI_API_KEY=" + key);

        WriteAtomically(file, lines);
    }

    /// <summary>Removes <c>GEMINI_API_KEY</c> from <c>~/.gemini/.env</c> (deleting the file if nothing else is in it). False if there was none.</summary>
    public static bool RemoveApiKey(string? home = null)
    {
        var file = EnvFile(home);
        if (!File.Exists(file)) return false;

        var lines = File.ReadAllLines(file).ToList();
        var removed = lines.RemoveAll(l => TryParseAssignment(l, out var name, out _) && name == "GEMINI_API_KEY") > 0;
        if (!removed) return false;

        if (lines.All(string.IsNullOrWhiteSpace)) File.Delete(file);
        else WriteAtomically(file, lines);
        return true;
    }

    /// <summary>
    /// Gives a CLI process the stored key when the environment it will inherit has none, so a headless run authenticates whatever
    /// the CLI's own rules for reading <c>~/.gemini/.env</c> are. The key lives only in that process's environment.
    /// </summary>
    /// <summary>True when the environment holds a Gemini API key, whether the user set it or it was applied from Settings.</summary>
    public static bool HasApiKey(IDictionary<string, string?> environment) =>
        ApiKeyVariables.Any(v => !string.IsNullOrWhiteSpace(environment.TryGetValue(v, out var value) ? value : null));

    public static void ApplyStoredApiKey(IDictionary<string, string?> environment, string? home = null)
    {
        if (ApiKeyVariables.Any(v => !string.IsNullOrWhiteSpace(environment.TryGetValue(v, out var existing) ? existing : null))) return;
        if (ReadStoredApiKey(home) is { } stored) environment[stored.Variable] = stored.Value;
    }

    // ---- installing the CLI ----

    /// <summary>
    /// Opens a console window that runs <c>npm install -g @google/gemini-cli</c>, so the install is visible and the user can see
    /// whether it worked. Throws <see cref="FileNotFoundException"/> when npm (Node.js) is not installed.
    /// </summary>
    public static Process? StartInstall()
    {
        if (SystemChecks.FindOnPath(OperatingSystem.IsWindows() ? new[] { "npm.cmd", "npm.exe" } : new[] { "npm" }) == null)
            throw new FileNotFoundException("npm was not found.");

        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"echo Installing the Gemini CLI. This can take a minute... & echo. & " + InstallCommand +
                                                   " & echo. & echo Finished. This page picks it up by itself; you can close this window. & pause\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return Process.Start(info);
    }

    // ---- helpers ----

    private static string Home(string? home) => home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>One <c>NAME=value</c> line of a dotenv file: blank lines and comments do not count, <c>export</c> and quotes are allowed.</summary>
    private static bool TryParseAssignment(string? line, out string name, out string value)
    {
        name = value = "";
        var text = (line ?? "").Trim();
        if (text.Length == 0 || text.StartsWith('#')) return false;
        if (text.StartsWith("export ", StringComparison.Ordinal)) text = text[7..].TrimStart();

        var eq = text.IndexOf('=');
        if (eq <= 0) return false;

        name = text[..eq].Trim();
        value = text[(eq + 1)..].Trim().Trim('"', '\'');
        return true;
    }

    private static void WriteAtomically(string file, IEnumerable<string> lines)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        File.WriteAllLines(temp, lines, new UTF8Encoding(false));
        try
        {
            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }
}
