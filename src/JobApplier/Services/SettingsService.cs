using JobApplier.Domain;
using JobApplier.Infrastructure;

namespace JobApplier.Services;

/// <summary>
/// Holds the current <see cref="AppSettings"/>. The instance returned by <see cref="Current"/> is treated as
/// immutable: to change settings, build a new object and call <see cref="Save"/>.
/// </summary>
public sealed class SettingsService
{
    private static readonly string[] Efforts = { "", "low", "medium", "high", "xhigh", "max" };
    private static readonly string[] Browsers = { "chrome", "msedge", "firefox", "webkit" };

    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsService(AppPaths paths)
    {
        _paths = paths;
        _current = Normalize(JsonFile.Read<AppSettings>(paths.SettingsFile) ?? new AppSettings());
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public string AccountsDocumentPath =>
        string.IsNullOrWhiteSpace(Current.AccountsDocumentPath) ? _paths.DefaultAccountsDocument : Current.AccountsDocumentPath;

    /// <summary>Validates and stores the settings. Returns an error message, or null on success.</summary>
    public string? Save(AppSettings settings)
    {
        settings = Normalize(settings);

        if (settings.ClaudePath.Length > 0 && !File.Exists(settings.ClaudePath))
            return "The Claude CLI path does not exist.";
        if (settings.CopilotPath.Length > 0 && !File.Exists(settings.CopilotPath))
            return "The GitHub Copilot CLI path does not exist.";

        if (settings.AccountsDocumentPath.Length > 0)
        {
            if (!settings.AccountsDocumentPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                return "The accounts document must be a .docx file.";
            var folder = Path.GetDirectoryName(settings.AccountsDocumentPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return "The folder of the accounts document does not exist.";
        }

        lock (_gate)
        {
            JsonFile.Write(_paths.SettingsFile, settings);
            _current = settings;
        }
        return null;
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.Provider = AiProvider.Normalize(s.Provider);
        s.CopilotPath = (s.CopilotPath ?? "").Trim().Trim('"');
        s.CopilotModel = (s.CopilotModel ?? "").Trim();
        s.ClaudePath = (s.ClaudePath ?? "").Trim().Trim('"');
        s.Model = (s.Model ?? "").Trim();
        var effort = (s.Effort ?? "").Trim().ToLowerInvariant();
        s.Effort = Efforts.Contains(effort) ? effort : "";
        var browser = (s.Browser ?? "").Trim().ToLowerInvariant();
        s.Browser = Browsers.Contains(browser) ? browser : "chrome";
        s.MaxParallelJobs = Math.Clamp(s.MaxParallelJobs, 1, 8);
        s.DefaultMinMatchPercent = Math.Clamp(s.DefaultMinMatchPercent, 0, 100);
        s.HelpTimeoutMinutes = Math.Clamp(s.HelpTimeoutMinutes, 2, 240);
        s.MailModel = (s.MailModel ?? "").Trim();
        s.MailAutoCheckHours = Math.Clamp(s.MailAutoCheckHours, 0, 72);
        s.AccountsDocumentPath = (s.AccountsDocumentPath ?? "").Trim().Trim('"');
        s.PlaywrightMcpVersion = string.IsNullOrWhiteSpace(s.PlaywrightMcpVersion) ? "0.0.85" : s.PlaywrightMcpVersion.Trim();
        return s;
    }
}
