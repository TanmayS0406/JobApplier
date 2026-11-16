using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JobApplier.Claude;

/// <summary>Result of <c>claude auth status</c>.</summary>
public sealed record ClaudeAuthStatus(bool CliFound, bool LoggedIn, string Detail, string? Plan = null);

/// <summary>
/// Thin wrapper around the official Claude Code CLI. JobApplier never handles Claude credentials itself:
/// sign-in (OAuth in the browser), token storage and refresh all happen inside the CLI, exactly like the
/// subscription login of Claude Code.
/// </summary>
public static class ClaudeCli
{
    private static readonly ConcurrentDictionary<int, Process> Running = new();

    /// <summary>Resolves the CLI from the configured path, PATH, or the default install folders. Null when not found.</summary>
    public static string? Locate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        var candidates = new List<string>();
        var exe = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in exe)
            {
                try { candidates.Add(Path.Combine(dir.Trim(), name)); }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude")); // native installer
        if (OperatingSystem.IsWindows()) candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));                // npm i -g

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Runs <c>claude auth status</c>.</summary>
    public static async Task<ClaudeAuthStatus> GetAuthStatusAsync(string? cliPath, CancellationToken cancellationToken)
    {
        if (cliPath == null) return new ClaudeAuthStatus(false, false, "Claude Code CLI not found.");

        string text;
        int exitCode;
        try
        {
            (exitCode, text) = await RunToEndAsync(cliPath, new[] { "auth", "status" }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ClaudeAuthStatus(true, false, ex.Message);
        }

        try
        {
            using var json = JsonDocument.Parse(text.Trim());
            var root = json.RootElement;
            var loggedIn = root.TryGetProperty("loggedIn", out var li) ? li.GetBoolean() : exitCode == 0;
            var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            var method = root.TryGetProperty("authMethod", out var m) ? m.GetString() : null;
            var plan = root.TryGetProperty("subscriptionType", out var s) ? s.GetString() : null;
            return new ClaudeAuthStatus(true, loggedIn, loggedIn ? email ?? method ?? "" : "Not signed in.", plan);
        }
        catch (JsonException)
        {
            // Older CLIs print plain text: rely on the exit code.
            return new ClaudeAuthStatus(true, exitCode == 0, exitCode == 0 ? "" : "Not signed in.");
        }
    }

    /// <summary>
    /// Opens a console window running <c>claude auth login</c>, which takes the user through the browser (OAuth) sign-in.
    /// </summary>
    public static Process? StartLogin(string cliPath)
    {
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + QuoteArgument(cliPath) + " auth login & echo. & pause\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return Process.Start(info);
    }

    /// <summary>
    /// Opens a console window with Claude Code itself running, starting with <paramref name="firstInput"/> (for example <c>/mcp</c>,
    /// Claude's own screen for connecting and signing in to connectors). The user finishes there; nothing is read back from it.
    /// </summary>
    public static Process? StartInteractive(string cliPath, string? firstInput)
    {
        var command = QuoteArgument(cliPath) + (string.IsNullOrEmpty(firstInput) ? "" : " " + QuoteArgument(firstInput));
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        return Process.Start(info);
    }

    public static async Task<int> LogoutAsync(string cliPath, CancellationToken cancellationToken)
    {
        var (exitCode, _) = await RunToEndAsync(cliPath, new[] { "auth", "logout" }, cancellationToken);
        return exitCode;
    }

    /// <summary>
    /// Builds a hidden, redirected process for the CLI. <c>.cmd</c> shims (npm installs) go through cmd.exe, so
    /// their arguments must not contain cmd metacharacters such as &amp; | &lt; &gt; ^.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string cliPath, IEnumerable<string> arguments, string workingDirectory)
    {
        var args = string.Join(" ", arguments.Select(QuoteArgument));
        var isShim = cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || cliPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var info = isShim
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + QuoteArgument(cliPath) + " " + args + "\"")
            : new ProcessStartInfo(cliPath, args);

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = new UTF8Encoding(false);
        info.StandardErrorEncoding = new UTF8Encoding(false);
        info.WorkingDirectory = workingDirectory;
        return info;
    }

    /// <summary>Starts a CLI process and remembers it so <see cref="KillAll"/> can clean up on shutdown.</summary>
    public static Process Start(ProcessStartInfo info)
    {
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Start();
        Running[process.Id] = process;
        process.Exited += (_, _) => Running.TryRemove(process.Id, out _);
        return process;
    }

    /// <summary>Kills the process and its children - the CLI spawns Playwright MCP, which spawns the browser.</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            try { process.Kill(); } catch { /* already gone */ }
        }
    }

    public static void KillAll()
    {
        foreach (var process in Running.Values) KillTree(process);
    }

    /// <summary>Quotes one argument following the CommandLineToArgvW rules.</summary>
    public static string QuoteArgument(string? arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;

        var sb = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }

    private static async Task<(int ExitCode, string Output)> RunToEndAsync(string cliPath, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var info = CreateStartInfo(cliPath, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        using var process = Start(info);
        process.StandardInput.Close();

        using var registration = cancellationToken.Register(() => KillTree(process));
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync(CancellationToken.None);

        cancellationToken.ThrowIfCancellationRequested();
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}
