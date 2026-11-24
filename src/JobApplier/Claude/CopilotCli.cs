using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JobApplier.Services;

namespace JobApplier.Claude;

/// <summary>
/// Thin wrapper around the GitHub Copilot CLI (<c>copilot</c>). Like the Claude side it never handles a credential itself: signing in is
/// the CLI's own OAuth flow (<c>copilot login</c>: your browser, or a one-time code), and the CLI keeps the token - in the system credential
/// store (Windows Credential Manager), or in <c>~/.copilot</c> when there is none.
/// <para>
/// The CLI has no "who am I" command, so whether someone is signed in is read from what a sign-in leaves behind (see
/// <see cref="HasStoredLogin"/>) or from the token variables the CLI also accepts. That says a sign-in exists, not that it is still valid: an
/// expired or revoked one shows up as a clear error on the first job.
/// </para>
/// </summary>
public static class CopilotCli
{
    /// <summary>The npm package that provides the CLI (needs Node.js 22 or newer).</summary>
    public const string PackageName = "@github/copilot";

    public const string NpmInstallCommand = "npm install -g " + PackageName;

    public const string WingetInstallCommand = "winget install GitHub.Copilot";

    /// <summary>The service name the CLI stores its OAuth token under in the system credential store.</summary>
    public const string CredentialService = "copilot-cli";

    /// <summary>Environment variables the CLI reads a GitHub token from, in the order it checks them (the first one set wins).</summary>
    public static readonly string[] TokenVariables = { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" };

    // The token kinds the CLI accepts: OAuth (gho_), GitHub App user-to-server (ghu_), fine-grained personal access token (github_pat_).
    // Only used to recognise that a plain-text config file holds a login; the value is never returned or logged.
    private static readonly Regex TokenShape = new(@"\b(?:gho_|ghu_|github_pat_)[A-Za-z0-9_]{16,}", RegexOptions.CultureInvariant);

    /// <summary>Resolves the CLI from the configured path, PATH, or where npm and winget put it. Null when not found.</summary>
    public static string? Locate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        var names = OperatingSystem.IsWindows() ? new[] { "copilot.exe", "copilot.cmd" } : new[] { "copilot" };
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // "App execution aliases" for Store apps live here as empty stubs and are not this CLI.
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in names)
            {
                try { candidates.Add(Path.Combine(dir.Trim(), name)); }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            // Where the installers put it. The running app's PATH does not know about a CLI installed after the app started.
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "copilot.cmd"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "copilot.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    // ---- is someone signed in? ----

    /// <summary>The name of the environment variable that holds a GitHub token, or null when there is none.</summary>
    public static string? TokenVariable(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return TokenVariables.FirstOrDefault(v => !string.IsNullOrWhiteSpace(environment(v)));
    }

    /// <summary>The folder the CLI keeps its settings and state in: <c>COPILOT_HOME</c>, else <c>.copilot</c> in the user profile.</summary>
    public static string ConfigFolder(string? userHome = null, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (environment("COPILOT_HOME") is { Length: > 0 } custom && userHome == null) return custom;
        return Path.Combine(userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
    }

    /// <summary>
    /// True when a sign-in made with <c>copilot login</c> is stored: an entry of the CLI in the system credential store, or - when there is no
    /// store and the CLI asked to keep the token in plain text - a token in its <c>config.json</c>.
    /// </summary>
    /// <param name="configFolder">The CLI's settings folder; null = the real one (the parameter is for tests).</param>
    /// <param name="credentialTargets">The names of the entries in the credential store; null = ask the system (for tests).</param>
    public static bool HasStoredLogin(string? configFolder = null, Func<IReadOnlyList<string>>? credentialTargets = null)
    {
        configFolder ??= ConfigFolder();
        if (ConfigHoldsToken(Path.Combine(configFolder, "config.json"))) return true;

        var targets = (credentialTargets ?? CredentialTargets)();
        return targets.Any(t => t.Contains(CredentialService, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ConfigHoldsToken(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 1_000_000) return false;
            return TokenShape.IsMatch(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The target names in the Windows Credential Manager (<c>cmdkey /list</c>), only those that mention the CLI. Never any secret.</summary>
    internal static IReadOnlyList<string> CredentialTargets()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            var info = new ProcessStartInfo("cmdkey.exe", "/list")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var process = Process.Start(info);
            if (process == null) return Array.Empty<string>();

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000) || !output.Wait(1000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { /* already gone */ }
                return Array.Empty<string>();
            }
            return output.Result.Split('\n').Select(l => l.Trim())
                .Where(l => l.Contains(CredentialService, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The names of the MCP servers the user set up for the CLI themselves (<c>mcp-config.json</c>). A job runs with none of them: they
    /// are switched off for the run, so only the browser and this app's own server are ever available to an agent.
    /// </summary>
    public static IReadOnlyList<string> UserMcpServerNames(string? configFolder = null)
    {
        try
        {
            var file = Path.Combine(configFolder ?? ConfigFolder(), "mcp-config.json");
            if (!File.Exists(file)) return Array.Empty<string>();
            return JsonNode.Parse(File.ReadAllText(file))?["mcpServers"] is JsonObject servers
                ? servers.Select(s => s.Key).ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Array.Empty<string>();   // a config the CLI itself cannot read is not ours to repair
        }
    }

    // ---- consoles the user finishes in ----

    /// <summary>
    /// Opens a console window running <c>copilot login</c>: the CLI opens the browser (or prints a one-time code to enter at
    /// github.com/login/device) and keeps the token itself.
    /// </summary>
    public static Process? StartLogin(string cliPath)
    {
        var note = "echo Sign in with GitHub in the browser, or enter the code shown below at github.com/login/device. When it says you are signed in, close this window.";
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + note + " & echo. & " + ClaudeCli.QuoteArgument(cliPath) + " login & echo. & pause\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return Process.Start(info);
    }

    /// <summary>
    /// Opens the CLI itself in a console: it has no sign-out command, only <c>/logout</c> typed inside it, so the window says so first.
    /// </summary>
    public static Process? StartSignOut(string cliPath)
    {
        var note = "echo To sign out, type /logout and press Enter. Then type /exit to leave. This does not sign you out of GitHub itself.";
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + note + " & echo. & " + ClaudeCli.QuoteArgument(cliPath) + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return Process.Start(info);
    }

    /// <summary>The installer this PC can use: winget (nothing else needed), else npm (needs Node.js 22 or newer). Null when neither exists.</summary>
    public static (string Name, string Command)? Installer()
    {
        if (SystemChecks.FindOnPath(OperatingSystem.IsWindows() ? new[] { "winget.exe" } : new[] { "winget" }) != null)
            return ("winget", WingetInstallCommand + " --accept-package-agreements --accept-source-agreements");
        if (SystemChecks.FindOnPath(OperatingSystem.IsWindows() ? new[] { "npm.cmd", "npm.exe" } : new[] { "npm" }) != null)
            return ("npm", NpmInstallCommand);
        return null;
    }

    /// <summary>
    /// Opens a console window that installs the CLI, so the install is visible and the user can see whether it worked.
    /// Throws <see cref="FileNotFoundException"/> when there is no installer (neither winget nor npm).
    /// </summary>
    public static Process? StartInstall()
    {
        var installer = Installer() ?? throw new FileNotFoundException("Neither winget nor npm was found.");
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"echo Installing the GitHub Copilot CLI. This can take a minute... & echo. & " + installer.Command +
                                                   " & echo. & echo Finished. This page picks it up by itself; you can close this window. & pause\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return Process.Start(info);
    }
}
