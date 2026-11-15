using JobApplier.Models;

namespace JobApplier.Services;

/// <summary>Pre-flight checks shown on the Settings page, so a missing prerequisite is found before the first job.</summary>
public static class SystemChecks
{
    public static SystemCheck Npx()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "npx.cmd", "npx.exe" } : new[] { "npx" };
        var found = FindOnPath(names);
        return new SystemCheck("Node.js (npx)", found != null, found ?? "Not found. Install Node.js 18+ from https://nodejs.org - it runs the Playwright MCP server.");
    }

    public static SystemCheck Browser(string browser)
    {
        if (browser is "firefox" or "webkit")
            return new SystemCheck("Browser: " + browser, true, "Installed on demand by Playwright.");

        var candidates = browser == "msedge"
            ? new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
            }
            : new[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
            };

        var path = candidates.FirstOrDefault(File.Exists);
        return new SystemCheck("Browser: " + (browser == "msedge" ? "Microsoft Edge" : "Google Chrome"), path != null,
            path ?? "Not found. Install it, or pick another browser below.");
    }

    public static SystemCheck Writable(string name, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return new SystemCheck(name, true, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SystemCheck(name, false, $"{folder}: {ex.Message}");
        }
    }

    public static SystemCheck WritableFile(string name, string file)
    {
        var folder = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(folder)) return new SystemCheck(name, false, "Invalid path.");
        var folderCheck = Writable(name, folder);
        return folderCheck.Ok ? new SystemCheck(name, true, file) : folderCheck;
    }

    private static string? FindOnPath(string[] names)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }
        return null;
    }
}
